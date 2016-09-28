using System;
using NLog;
using NLog.Targets;
using Zyborg.Logentries;

namespace Zyborg.NLog.Targets.Logentries
{
    [Target("Logentries")]
    public sealed class LogentriesTarget : TargetWithLayout
    {
        private LogentriesAsyncLogger logentriesAsync;

        public LogentriesTarget()
        {
            try {
                logentriesAsync = new LogentriesAsyncLogger();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        
        /// Debug flag.
        public bool Debug 
        {
            get { return logentriesAsync.Debug; }
            set { logentriesAsync.Debug = value; } 
        }

        /// Is using DataHub parameter flag. - 
        /// set to true if it is needed to send messages to DataHub instance.
        public bool IsUsingDataHub
        {
            get { return logentriesAsync.UseDataHub; }
            set { logentriesAsync.UseDataHub = value; }
        }

        /// DataHub server address
        public String DataHubAddr
        {
            get { return logentriesAsync.DataHubAddr; }
            set { logentriesAsync.DataHubAddr = value; }
        }

        /// DataHub server port
        public int DataHubPort
        {
            get { return logentriesAsync.DataHubPort; }
            set { logentriesAsync.DataHubPort = value; }
        }

        /// Option to set Token programmatically or in Appender Definition
        public string Token
        {
            get { return logentriesAsync.Token; }
            set { logentriesAsync.Token = value; }
        }

        /// HTTP PUT Flag
        public bool HttpPut
        {
            get { return logentriesAsync.UseHttpPut; }
            set { logentriesAsync.UseHttpPut = value; }
        }

        /// SSL/TLS parameter flag
        public bool Ssl
        {
            get { return logentriesAsync.UseSsl; }
            set { logentriesAsync.UseSsl = value; }
        }

        /// ACCOUNT_KEY parameter for HTTP PUT logging
        public String Key
        {
            get { return logentriesAsync.AccountKey; }
            set { logentriesAsync.AccountKey = value; }
        }

        /// LOCATION parameter for HTTP PUT logging
        public String Location
        {
            get { return logentriesAsync.Location; }
            set { logentriesAsync.Location = value; }
        }

        /// LogHostname - switch that defines whether add host name to the log message
        public bool LogHostname
        {
            get { return logentriesAsync.UseHostName; }
            set { logentriesAsync.UseHostName = value; }
        }

        /// HostName - user-defined host name.
        /// If empty the library will try to obtain it automatically
        public String HostName
        {
            get { return logentriesAsync.HostName; }
            set { logentriesAsync.HostName = value; }
        }

        /// User-defined log message ID
        public String LogID
        {
            get { return logentriesAsync.LogID; }
            set { logentriesAsync.LogID = value; }
        }

        public bool KeepConnection
        { get; set; }

        protected override void Write(LogEventInfo logEvent)
        {
            //Render message content
            String renderedEvent = this.Layout.Render(logEvent);

            logentriesAsync.AddLine(renderedEvent);
        }

        protected override void CloseTarget()
        {
            base.CloseTarget();

            logentriesAsync.interruptWorker();
        }
    }
}
