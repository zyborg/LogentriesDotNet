using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace Zyborg.Logentries
{

    public class LogentriesAsyncLogger
    {
#region -- Constants -- 

        // Current version number.
        public const String Version = "2.6.7";

        // Size of the internal event queue. 
        public const int QUEUE_SIZE = 32768;

        // Minimal delay between attempts to reconnect in milliseconds. 
        public const int MIN_DELAY = 100;

        // Maximal delay between attempts to reconnect in milliseconds. 
        public const int MAX_DELAY = 10000;

        // Appender signature - used for debugging messages. 
        public const String LE_SIGNATURE = "LE: ";

        // Legacy Logentries configuration names. 
        public const String LegacyConfigTokenName = "LOGENTRIES_TOKEN";
        public const String LegacyConfigAccountKeyName = "LOGENTRIES_ACCOUNT_KEY";
        public const String LegacyConfigLocationName = "LOGENTRIES_LOCATION";

        // New Logentries configuration names.
        public const String ConfigTokenName = "Logentries.Token";
        public const String ConfigAccountKeyName = "Logentries.AccountKey";
        public const String ConfigLocationName = "Logentries.Location";

        // Error message displayed when invalid token is detected. 
        protected const String InvalidTokenMessage = "\n\nIt appears your LOGENTRIES_TOKEN value is invalid or missing.\n\n";

        // Error message displayed when invalid account_key or location parameters are detected. 
        protected const String InvalidHttpPutCredentialsMessage = "\n\nIt appears your LOGENTRIES_ACCOUNT_KEY or LOGENTRIES_LOCATION values are invalid or missing.\n\n";

        // Error message deisplayed when queue overflow occurs. 
        protected const String QueueOverflowMessage = "\n\nLogentries buffer queue overflow. Message dropped.\n\n";

        // Newline char to trim from message for formatting. 
        protected static char[] _trimChars = { '\r', '\n' };

        /** Non-Unix and Unix Newline */
        protected static string[] _posixNewline = { "\r\n", "\n" };

        /** Unicode line separator character */
        protected static string _lineSeparator = "\u2028";

        // Restricted symbols that should not appear in host name.
        // See http://support.microsoft.com/kb/228275/en-us for details.
        private static Regex _forbiddenHostNameChars = new Regex(
                @"[/\\\[\]\""\:\;\|\<\>\+\=\,\?\* _]{1,}", RegexOptions.Compiled);

#endregion -- Constants --

        protected readonly BlockingCollection<string> _queue;
        protected readonly CancellationTokenSource _cancel = new CancellationTokenSource();

        protected IConfiguration _config;

        protected readonly Thread _workerThread;
        protected readonly Random _random = new Random();

        private LogentriesClient _leClient = null;
        protected bool _isRunning = false;

#region -- Singletons --

        // UTF-8 output character set. 
        //protected static readonly UTF8Encoding UTF8 = new UTF8Encoding();

        // ASCII character set used by HTTP. 
        //protected static readonly ASCIIEncoding ASCII = new ASCIIEncoding();

        //static list of all the queues the le appender might be managing.
        private static ConcurrentBag<BlockingCollection<string>> _allQueues =
                new ConcurrentBag<BlockingCollection<string>>();

        /// <summary>
        /// Determines if the queue is empty after waiting the specified waitTime.
        /// Returns true or false if the underlying queues are empty.
        /// </summary>
        /// <param name="waitTime">The length of time the method should block before giving up
        ///    waiting for it to empty.</param>
        /// <returns>True if the queue is empty, false if there are still items waiting to be
        ///    written.</returns>
        public static bool AreAllQueuesEmpty(TimeSpan waitTime)
        {
            var start = DateTime.UtcNow;
            var then = DateTime.UtcNow;

            while (start.Add(waitTime) > then)
            {
                if (_allQueues.All(x => x.Count == 0))
                    return true;

                Thread.Sleep(100);
                then = DateTime.UtcNow;
            }

            return _allQueues.All(x => x.Count == 0);
        }

        public IConfiguration BuildDefaultConfiguration()
        {
            var cb = new ConfigurationBuilder()
                    .AddEnvironmentVariables();
            return cb.Build();
        }

#endregion -- Singletons --

        public LogentriesAsyncLogger(IConfiguration config = null)
        {
            _queue = new BlockingCollection<string>(QUEUE_SIZE);
            _allQueues.Add(_queue);

            _config = config;
            if (_config == null)
                _config = BuildDefaultConfiguration();

            _workerThread = new Thread(new ThreadStart(Run));
            _workerThread.Name = "Logentries Log Appender";
            _workerThread.IsBackground = true;
        }

#region -- Configuration Properties --

        public string Token
        { get; set; }

        public string AccountKey
        { get; set; }

        public string Location
        { get; set; }

        public bool ImmediateFlush
        { get; set; }

        public bool Debug
        { get; set; }

        public bool UseHttpPut
        { get; set; }

        public bool UseSsl
        { get; set; }

        // Properties for defining location of DataHub instance if one is used.

        /// By default Logentries service is used instead of DataHub instance.
        public bool UseDataHub
        { get; set; }

        public string DataHubAddr
        { get; set; }
        public int DataHubPort
        { get; set; }


        // Properties to define host name of user's machine and define user-specified log ID.

        /// Defines whether to prefix log message with HostName or not.
        public bool UseHostName
        { get; set; }
        
        /// User-defined or auto-defined host name (if not set in config. file)
        public string HostName
        { get; set; }

        /// User-defined log ID to be prefixed to the log message.
        public string LogID
        { get; set; }

#endregion -- Configuration Properties --

#region -- Protected methods --

        protected virtual void Run()
        {
            var ct = _cancel.Token;
            ct.ThrowIfCancellationRequested();

            try
            {
                // Open connection.
                ReopenConnection();
                ct.ThrowIfCancellationRequested();

                string logMessagePrefix = String.Empty;

                if (UseHostName)
                {
                    // If LogHostName is set to "true", but HostName is not defined -
                    // try to get host name from Environment.
                    if (string.IsNullOrWhiteSpace(HostName))
                    {
                        try
                        {
                            WriteDebugMessages("HostName parameter is not defined "
                                    + "- trying to get it from System.Environment.MachineName");
                            HostName = "HostName={System.Environment.MachineName} ";
                        }
                        catch (InvalidOperationException )
                        {
                            // Cannot get host name automatically, so assume that HostName is not used
                            // and log message is sent without it.
                            UseHostName = false;
                            WriteDebugMessages("Failed to get HostName parameter using"
                                    + " System.Environment.MachineName. Log messages will"
                                    + " not be prefixed by HostName");
                        }
                    }
                    else
                    {
                        if (!CheckIfHostNameValid(HostName))
                        {
                            // If user-defined host name is incorrect - we cannot use it
                            // and log message is sent without it.
                            UseHostName = false;
                            WriteDebugMessages("HostName parameter contains prohibited characters."
                                    + " Log messages will not be prefixed by HostName");
                        }
                        else
                        {
                            HostName = $"HostName={HostName} ";
                        }
                    }
                }

                if (! string.IsNullOrWhiteSpace(LogID))
                {
                    logMessagePrefix = $"{LogID} ";
                }

                if (UseHostName)
                {
                    logMessagePrefix += HostName;
                }

                // Flag that is set if logMessagePrefix is empty.
                bool isPrefixEmpty = string.IsNullOrWhiteSpace(logMessagePrefix);

                // Send data in queue.
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
    
                    // added debug here
                    WriteDebugMessages("Await queue data");

                    // Take data from queue.
                    var line = _queue.Take();
                    //added debug message here
                    WriteDebugMessages("Queue data obtained");

                    // Replace newline chars with line separator to format multi-line events nicely.
                    foreach (String newline in _posixNewline)
                    {
                        line = line.Replace(newline, _lineSeparator);
                    }

                    // If m_UseDataHub == true (logs are sent to DataHub instance) then m_Token is not
                    // appended to the message.
                    string finalLine = ((!UseHttpPut && !UseDataHub) ? this.Token + line : line) + '\n';

                    // Add prefixes: LogID and HostName if they are defined.
                    if (!isPrefixEmpty)
                    {
                        finalLine = logMessagePrefix + finalLine;
                    }

                    byte[] data = Encoding.UTF8.GetBytes(finalLine);

                    // Send data, reconnect if needed.
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            //removed iff loop and added debug message
                            // Le.Client writes data
                            WriteDebugMessages("Write data");
                            this._leClient.Write(data, 0, data.Length);

                            WriteDebugMessages("Write complete, flush");

                            // if (m_ImmediateFlush) was removed, always flushed now.
                            this._leClient.Flush();

                            WriteDebugMessages("Flush complete");
                        }
                        catch (IOException e)
                        {
                            ct.ThrowIfCancellationRequested();

                            WriteDebugMessages("IOException during write, reopen: " + e.Message);
                            // Reopen the lost connection.
                            ReopenConnection();
                            continue;
                        }

                        break;
                    }
                }
            }
            // TODO: This isn't reintroduced to .NET Core till .NET Standard 2.0 
            //catch (ThreadInterruptedException ex)
            catch (OperationCanceledException ex)
            {
                WriteDebugMessages("Logentries asynchronous socket client was interrupted.", ex);
            }
        }

        protected virtual void OpenConnection()
        {
            try
            {
                if (_leClient == null)
                {
                    // Create LeClient instance providing all needed parameters. If DataHub-related properties
                    // have not been overridden by log4net or NLog configurators, then DataHub is not used, 
                    // because m_UseDataHub == false by default.
                    _leClient = new LogentriesClient(UseHttpPut, UseSsl, UseDataHub, DataHubAddr, DataHubPort);
                }

                _leClient.Connect();

                if (UseHttpPut)
                {
                    var header = String.Format("PUT /{0}/hosts/{1}/?realtime=1 HTTP/1.1\r\n\r\n",
                            AccountKey, Location);

                    _leClient.Write(Encoding.ASCII.GetBytes(header), 0, header.Length);
                }
            }
            catch (Exception ex)
            {
                throw new IOException("An error occurred while opening the connection.", ex);
            }
        }

        protected virtual void ReopenConnection()
        {
            WriteDebugMessages("ReopenConnection");
            CloseConnection();

            var rootDelay = MIN_DELAY;
            while (true)
            {
                try
                {
                    OpenConnection();

                    return;
                }
                catch (Exception ex)
                {
                    if (Debug)
                        WriteDebugMessages("Unable to connect to Logentries API.", ex);
                }

                rootDelay *= 2;
                if (rootDelay > MAX_DELAY)
                    rootDelay = MAX_DELAY;

                var waitFor = rootDelay + _random.Next(rootDelay);

                try
                {
                    Thread.Sleep(waitFor);
                }
                catch
                {
                    // TODO: This isn't reintroduced to .NET Core till .NET Standard 2.0 
                    //throw new ThreadInterruptedException();
                    throw new Exception();
                }
            }
        }

        protected virtual void CloseConnection()
        {
            if (_leClient != null)
                _leClient.Close();
        }

        public string GetSetting(string name)
        {
            return _config?[name];
        }

        // /* Retrieve configuration settings
        //  * Will check Enviroment Variable as the last fall back.
        //  * 
        //  */
        // private string retrieveSetting(String name)
        // {
        //     string cloudconfig = null;
        //     if (Environment.OSVersion.Platform == PlatformID.Unix)
        //     {
        //         cloudconfig = ConfigurationManager.AppSettings.Get(name);
        //     }
        //     else
        //     {
        //         cloudconfig = CloudConfigurationManager.GetSetting(name);
        //     }



        //     if (!String.IsNullOrWhiteSpace(cloudconfig))
        //     {
        //         WriteDebugMessages(String.Format("Found Cloud Configuration settings for {0}", name));
        //         return cloudconfig;
        //     }

        //     var appconfig = ConfigurationManager.AppSettings[name];
        //     if (!String.IsNullOrWhiteSpace(appconfig))
        //     {
        //         WriteDebugMessages(String.Format("Found App Settings for {0}", name));
        //         return appconfig;
        //     }

        //     var envconfig = Environment.GetEnvironmentVariable(name);
        //     if (!String.IsNullOrWhiteSpace(envconfig))
        //     {
        //         WriteDebugMessages(String.Format("Found Enviromental Variable for {0}", name));
        //         return envconfig;
        //     }
        //     WriteDebugMessages(String.Format("Unable to find Logentries Configuration Setting for {0}.", name));
        //     return null;
        // }

        /*
         * Use CloudConfigurationManager with .NET4.0 and fallback to System.Configuration for previous frameworks.
         * 
         *       
         *       One issue is that there are two appsetting keys for each setting - the "legacy" key, such as "LOGENTRIES_TOKEN"
         *       and the "non-legacy" key, such as "Logentries.Token".  Again, I'm not sure of the reasons behind this, so the code below checks
         *       both the legacy and non-legacy keys, defaulting to the legacy keys if they are found.
         *       
         *       It probably should be investigated whether the fallback to ConfigurationManager is needed at all, as CloudConfigurationManager 
         *       will retrieve settings from appSettings in a non-Azure environment.
         */
        public virtual bool LoadCredentials()
        {
            if (!UseHttpPut)
            {
                if (GetIsValidGuid(Token))
                    return true;

                var configToken = GetSetting(LegacyConfigTokenName)
                        ?? GetSetting(ConfigTokenName);

                if (!String.IsNullOrEmpty(configToken)
                        && GetIsValidGuid(configToken))
                {
                    Token = configToken;
                    return true;
                }
                WriteDebugMessages(InvalidTokenMessage);
                return false;
            }

            if (!string.IsNullOrEmpty(AccountKey)
                    && GetIsValidGuid(AccountKey)
                    && !string.IsNullOrEmpty(Location))
                return true;

            var configAccountKey = GetSetting(LegacyConfigAccountKeyName)
                    ?? GetSetting(ConfigAccountKeyName);

            if (!string.IsNullOrEmpty(configAccountKey)
                    && GetIsValidGuid(configAccountKey))
            {
                AccountKey = configAccountKey;
                var configLocation = GetSetting(LegacyConfigLocationName)
                        ?? GetSetting(ConfigLocationName);

                if (!string.IsNullOrEmpty(configLocation))
                {
                    Location = configLocation;
                    return true;
                }
            }
            WriteDebugMessages(InvalidHttpPutCredentialsMessage);
            return false;
        }

        private bool CheckIfHostNameValid(String hostName)
        {
            return !_forbiddenHostNameChars.IsMatch(hostName); // Returns false if reg.ex. matches any of forbidden chars.
        }


        protected virtual bool GetIsValidGuid(string guidString)
        {
            if (String.IsNullOrEmpty(guidString))
                return false;

            System.Guid newGuid = System.Guid.NewGuid();

            return System.Guid.TryParse(guidString, out newGuid);
        }

        protected virtual void WriteDebugMessages(string message, Exception ex)
        {
            if (!Debug)
                return;

            message = LE_SIGNATURE + message;
            string[] messages = { message, ex.ToString() };
            foreach (var msg in messages)
            {
                Trace.WriteLine(msg);
            }
        }

        protected virtual void WriteDebugMessages(string message)
        {
            if (!Debug)
                return;

            message = LE_SIGNATURE + message;

            Trace.WriteLine(message);
        }

#endregion -- Protected methods --

#region -- Public Methods --

        public virtual void AddLine(string line)
        {
            WriteDebugMessages("Adding Line: " + line);
            if (!_isRunning)
            {
                // We need to load user credentials only
                // if the configuration does not state that DataHub is used;
                // credentials needed only if logs are sent to LE service directly.
                bool credentialsLoaded = false;
                if (!UseDataHub)
                {
                    credentialsLoaded = LoadCredentials();
                }

                // If in DataHub mode credentials are ignored.
                if (credentialsLoaded || UseDataHub)
                {
                    WriteDebugMessages("Starting Logentries asynchronous socket client.");
                    _workerThread.Start();
                    _isRunning = true;
                }
            }

            WriteDebugMessages("Queueing: " + line);

            String trimmedEvent = line.TrimEnd(_trimChars);

            // Try to append data to queue.
            if (!_queue.TryAdd(trimmedEvent))
            {
                _queue.Take();
                if (!_queue.TryAdd(trimmedEvent))
                    WriteDebugMessages(QueueOverflowMessage);
            }
        }

        public void interruptWorker()
        {
            _cancel.Cancel();
            //_workerThread.Interrupt();
        }

#endregion -- Public Methods --
    }
}
