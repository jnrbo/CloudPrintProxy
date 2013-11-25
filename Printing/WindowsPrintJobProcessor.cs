﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TSVCEO.CloudPrint.Util;
using System.Security.Principal;
using System.Printing;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Security;

namespace TSVCEO.CloudPrint.Printing
{
    public class WindowsPrintJobProcessor : IPrintJobProcessor
    {
        private const int RequeueTime = 15000;

        #region private properties

        private bool Disposed { get; set; }
        private bool Running { get; set; }
        private ConcurrentQueue<CloudPrintJob> PrintJobQueue { get; set; }
        private ConcurrentQueue<CloudPrintJob> DeferredJobQueue { get; set; }
        private ConcurrentQueue<CloudPrintJob> UserJobNotifierQueue { get; set; }
        private AutoResetEvent PrintJobsUpdated { get; set; }
        private Task PrintQueueProcessorTask { get; set; }
        private Timer DeferredPrintQueueTimer { get; set; }
        private Task UserNotifierTask { get; set; }
        private CancellationTokenSource CancelTokenSource { get; set; }
        private Dictionary<string, DateTime> UsersNotifiedToLogin { get; set; }

        #endregion

        #region constructor / destructor

        public WindowsPrintJobProcessor()
        {
            PrintJobsUpdated = new AutoResetEvent(false);
            PrintJobQueue = new ConcurrentQueue<CloudPrintJob>();
            DeferredJobQueue = new ConcurrentQueue<CloudPrintJob>();
            UserJobNotifierQueue = new ConcurrentQueue<CloudPrintJob>();
            UsersNotifiedToLogin = new Dictionary<string, DateTime>();
        }

        ~WindowsPrintJobProcessor()
        {
            Dispose(false);
        }

        #endregion

        #region private methods

        private void Dispose(bool disposing)
        {
            if (DeferredPrintQueueTimer != null)
            {
                DeferredPrintQueueTimer.Dispose();
            }

            if (CancelTokenSource != null)
            {
                CancelTokenSource.Cancel();

                if (PrintQueueProcessorTask != null)
                {
                    PrintQueueProcessorTask.Wait();
                    PrintQueueProcessorTask.Dispose();
                }

                if (UserNotifierTask != null)
                {
                    UserNotifierTask.Wait();
                    UserNotifierTask.Dispose();
                }

                CancelTokenSource.Dispose();
            }

            DeferredPrintQueueTimer = null;
            PrintQueueProcessorTask = null;
            UserNotifierTask = null;
            CancelTokenSource = null;

            if (disposing)
            {
                Disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        private IEnumerable<CloudPrintJob> DequeueDeferredPrintQueueJobs()
        {
            lock (DeferredJobQueue)
            {
                CloudPrintJob job;
                while (DeferredJobQueue.TryDequeue(out job))
                {
                    yield return job;
                }
            }
        }

        private void ProcessPrintJob(CloudPrintJob job)
        {
            if (!WindowsIdentityStore.IsAcceptedDomain(job.Domain))
            {
                Logger.Log(LogLevel.Debug, "Job {0} deferred because {1}@{2} is not in an accepted domain", job.JobID, job.Username, job.Domain);
            }
            else if (!WindowsIdentityStore.HasWindowsIdentity(job.Username))
            {
                Logger.Log(LogLevel.Debug, "Job {0} deferred because {1} has not logged in", job.JobID, job.Username);
            }
            else
            {
                job.SetStatus(CloudPrintJobStatus.IN_PROGRESS);
                try
                {
                    Logger.Log(LogLevel.Info, "Starting job {0}", job.JobID);
                    using (JobPrinter printer = Activator.CreateInstance(job.Printer.GetJobPrinterType()) as JobPrinter)
                    {
                        printer.Print(job);
                    }
                    Logger.Log(LogLevel.Info, "Job {0} Finished", job.JobID);
                    job.SetStatus(CloudPrintJobStatus.DONE);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Info, "Job {0} in error:\nException:\n{1}\n{2}", job.JobID, ex.Message, ex.ToString());
                    job.SetError(ex.GetType().Name, ex.Message);
                }
            }
        }

        private void PrintJobQueueProcessor(CancellationToken cancelToken)
        {
            try
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { this.PrintJobsUpdated, cancelToken.WaitHandle }) == 0)
                {
                    CloudPrintJob job;
                    while (!cancelToken.IsCancellationRequested && PrintJobQueue.TryDequeue(out job))
                    {
                        if (job.Status == CloudPrintJobStatus.QUEUED)
                        {
                            ProcessPrintJob(job);
                        }

                        if (job.Status == CloudPrintJobStatus.QUEUED)
                        {
                            lock (DeferredJobQueue)
                            {
                                DeferredJobQueue.Enqueue(job);
                            }

                            UserJobNotifierQueue.Enqueue(job);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "WindowsPrintJobProcessor task caught exception {0}\n{1}", ex.Message, ex.ToString());
            }
        }

        private void SendEmail(string email, string subject, string message)
        {
            if (Config.MailServerHost != null)
            {
                SmtpClient client = new SmtpClient(Config.MailServerHost, Config.MailServerPort);
                client.EnableSsl = Config.MailServerUseSSL;

                string mailfrom = Config.MailFrom ?? Config.OAuthEmail;

                if (Config.MailServerUseAuth)
                {
                    string adminemail = Config.MailServerUsername ?? Config.MailFrom ?? Config.OAuthEmail;
                    string adminusername = adminemail.Split('@').First();
                    SecureString password = WindowsIdentityStore.GetUserCredential(adminusername);
                    client.Credentials = new NetworkCredential(adminemail, password);
                }

                Logger.Log(LogLevel.Info, "Sending email\n\nFrom: {0}\nTo: {1}\nSubject: {2}\n\n{3}",
                    mailfrom,
                    email,
                    subject,
                    message
                );

                client.Send(mailfrom, email, subject, message);
            }
        }

        private void NotifyUserToLogin(CloudPrintJob job)
        {
            if (!UsersNotifiedToLogin.ContainsKey(job.Username) || UsersNotifiedToLogin[job.Username] < DateTime.Now.AddHours(-24))
            {
                if (job.CreateTime > DateTime.Now.AddDays(-3))
                {
                    try
                    {
                        string message = String.Format(
                            "You have sent a job to cloud printer \"{0}\" on {1} at {2} on {3}\n\nPlease log into http://{4}:{5}/ to allow this job (and any others) to be printed.",
                            job.Printer.Name,
                            Environment.MachineName,
                            job.CreateTime.ToLocalTime().ToString("hh:mm tt"),
                            job.CreateTime.ToLocalTime().ToString("dd MMM yyyy"),
                            Config.UserAuthHost,
                            Config.UserAuthHttpPort
                        );
                        string subject = String.Format("Please log in to enable cloud printing on {0}", Environment.MachineName);
                        UsersNotifiedToLogin[job.Username] = DateTime.Now;
                        SendEmail(job.OwnerId, subject, message);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Warning, "Error notifying user to log in\n\n{0}", ex.ToString());
                    }
                }
            }
        }

        private void UserNotifier(CancellationToken cancelToken)
        {
            try
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { this.PrintJobsUpdated, cancelToken.WaitHandle }) == 0)
                {
                    CloudPrintJob job;

                    while (!cancelToken.IsCancellationRequested && PrintJobQueue.TryDequeue(out job))
                    {
                        if (job.Status == CloudPrintJobStatus.QUEUED)
                        {
                            NotifyUserToLogin(job);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "UserNotifier task caught exception {0}\n{1}", ex.Message, ex.ToString());
            }
        }

        private void ThrowIfDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }
        }

        #endregion

        #region public methods

        public void Start()
        {
            ThrowIfDisposed();

            if (!Running)
            {
                if (CancelTokenSource == null)
                {
                    CancelTokenSource = new CancellationTokenSource();
                }

                CancellationToken cancelToken = CancelTokenSource.Token;

                if (PrintQueueProcessorTask == null)
                {
                    PrintQueueProcessorTask = Task.Factory.StartNew(() => PrintJobQueueProcessor(cancelToken), cancelToken);
                }

                if (UserNotifierTask == null)
                {
                    UserNotifierTask = Task.Factory.StartNew(() => UserNotifier(cancelToken), cancelToken);
                }

                if (DeferredPrintQueueTimer == null)
                {
                    DeferredPrintQueueTimer = new Timer(new TimerCallback((obj) => AddJobs(DequeueDeferredPrintQueueJobs())), null, RequeueTime, RequeueTime);
                }
            }
        }

        public void Stop()
        {
            ThrowIfDisposed();

            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void AddJob(CloudPrintJob job)
        {
            ThrowIfDisposed();

            PrintJobQueue.Enqueue(job);
            PrintJobsUpdated.Set();
        }

        public void AddJobs(IEnumerable<CloudPrintJob> jobs)
        {
            ThrowIfDisposed();

            foreach (var job in jobs)
            {
                PrintJobQueue.Enqueue(job);
            }
            PrintJobsUpdated.Set();
        }

        public IEnumerable<CloudPrinter> GetPrintQueues()
        {
            using (var server = new LocalPrintServer())
            {
                return server.GetPrintQueues().Where(q => q.IsShared).Select(q => new CloudPrinterImpl(q));
            }
        }

        public IEnumerable<CloudPrintJob> GetQueuedJobs()
        {
            return DeferredJobQueue.AsEnumerable().Union(PrintJobQueue.AsEnumerable());
        }

        #endregion
    }
}
