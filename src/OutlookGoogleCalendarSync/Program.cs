﻿using log4net;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OutlookGoogleCalendarSync {
    /// <summary>
    /// Class with program entry point.
    /// </summary>
    internal sealed class Program {
        public static string UserFilePath;
        private static readonly ILog log = LogManager.GetLogger(typeof(Program));
        private const string logSettingsFile = "logger.xml";
        private const string defaultLogFilename = "OGcalsync.log";
        public static String WorkingFilesDirectory;
        public static log4net.Core.Level MyFailLevel = new log4net.Core.Level(65000, "FAIL"); //An error but not one for reporting
        //log4net.Core.Level.Fine == log4net.Core.Level.Debug (30000), so manually changing its value
        public static log4net.Core.Level MyFineLevel = new log4net.Core.Level(25000, "FINE");
        public static log4net.Core.Level MyUltraFineLevel = new log4net.Core.Level(24000, "ULTRA-FINE"); //Logs email addresses

        public static Boolean StartedWithFileArgs = false;
        public static String Title { get; private set; }
        public static Boolean StartedWithSquirrelArgs {
            get {
                String[] cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                return (cliArgs.Length == 2 && cliArgs[0].ToLower().StartsWith("--squirrel"));
            }
        }
        /// <summary>
        /// The OGCS directory within user's roaming profile
        /// </summary>
        public static String RoamingProfileOGCS;

        private static Boolean? isInstalled = null;
        public static Boolean IsInstalled {
            get {
                isInstalled = isInstalled ?? Updater.IsSquirrelInstall();
                return (Boolean)isInstalled;
            }
        }
        public static Updater Updater;

        [STAThread]
        private static void Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try {
                GoogleOgcs.ErrorReporting.Initialise();

                RoamingProfileOGCS = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Application.ProductName);
                parseArgumentsAndInitialise(args);

                Updater.MakeSquirrelAware();
                Forms.Splash.ShowMe();

                log.Debug("Loading settings from file.");
                Settings.Load();
                Settings.Instance.Proxy.Configure();

                Updater = new Updater();
                isNewVersion(Program.IsInstalled);
                Updater.CheckForUpdate();

                TimezoneDB.Instance.CheckForUpdate();

                try {
                    String startingTab = Settings.Instance.LastSyncDate == new DateTime(0) ? "Help" : null;
                    Application.Run(new Forms.Main(startingTab));
                } catch (ApplicationException ex) {
                    String reportError = ex.Message;
                    log.Fatal(reportError);
                    if (ex.InnerException != null) {
                        reportError = ex.InnerException.Message;
                        log.Fatal(reportError);
                    }
                    MessageBox.Show(reportError, "Application terminated!", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    throw new ApplicationException(ex.Message.StartsWith("COM error") ? "Suggest startup delay" : "");

                } catch (System.Runtime.InteropServices.COMException ex) {
                    OGCSexception.Analyse(ex);
                    throw new ApplicationException("Suggest startup delay");

                } catch (System.Exception ex) {
                    OGCSexception.Analyse(ex, true);
                    log.Fatal("Application unexpectedly terminated!");
                    MessageBox.Show(ex.Message, "Application unexpectedly terminated!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    throw new ApplicationException();
                }

            } catch (ApplicationException aex) {
                if (aex.Message == "Suggest startup delay") {
                    if (isCLIstartup() && Settings.Instance.StartOnStartup) {
                        log.Debug("Suggesting to set a startup delay.");
                        MessageBox.Show("If this error only happens when logging in to Windows, try " +
                            ((Settings.Instance.StartupDelay == 0) ? "setting a" : "increasing the") + " delay for OGCS on startup.",
                            "Set a delay on startup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                } else
                    MessageBox.Show(aex.Message, "Application terminated!", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);

                log.Warn("Tidying down any remaining Outlook references, as OGCS crashed out.");
                OutlookOgcs.Calendar.Disconnect();
            }
            Forms.Splash.CloseMe();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            while (Updater != null && Updater.IsBusy) {
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);
            }
            log.Info("Application closed.");
        }

        private static void parseArgumentsAndInitialise(string[] args) {
            //We're interested in non-Squirrel arguments here, ie ones which don't start with Linux-esque dashes (--squirrel)
            StartedWithFileArgs = (args.Length != 0 && args.Count(a => a.StartsWith("/") && !a.StartsWith("/d")) != 0);

            if (args.Contains("/?") || args.Contains("/help", StringComparer.OrdinalIgnoreCase)) {
                OgcsMessageBox.Show("Command line parameters:-\r\n" +
                    "  /?\t\tShow options\r\n" +
                    "  /l:OGcalsync.log\tFile to log to\r\n" +
                    "  /s:settings.xml\tSettings file to use.\r\n\t\tFile created with defaults if it doesn't exist\r\n" +
                    "  /d:60\t\tSeconds startup delay\r\n" +
                    "  /t:\"Config A\"\tAppend custom text to application title",
                    "OGCS command line parameters", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.Exit(0);
            }

            Dictionary<String, String> loggingArg = parseArgument(args, 'l');
            initialiseLogger(loggingArg["Filename"], loggingArg["Directory"], bootstrap: true);

            Dictionary<String, String> settingsArg = parseArgument(args, 's');
            Settings.InitialiseConfigFile(settingsArg["Filename"], settingsArg["Directory"]);
            
            log.Info("Storing user files in directory: " + UserFilePath);

            //Before settings have been loaded, early config of cloud logging
            GoogleOgcs.ErrorReporting.UpdateLogUuId();
            Boolean cloudLogSetting = false;
            String cloudLogXmlSetting = XMLManager.ImportElement("CloudLogging", Settings.ConfigFile);
            if (!string.IsNullOrEmpty(cloudLogXmlSetting)) cloudLogSetting = Boolean.Parse(cloudLogXmlSetting);
            GoogleOgcs.ErrorReporting.SetThreshold(cloudLogSetting);

            if (!StartedWithFileArgs) {
                //Now let's confirm files are actually in the right place
                Boolean keepPortable = (XMLManager.ImportElement("Portable", Settings.ConfigFile) ?? "false").Equals("true");
                if (keepPortable) {
                    if (UserFilePath != System.Windows.Forms.Application.StartupPath) {
                        log.Info("File storage location is incorrect according to " + Settings.ConfigFile);
                        MakePortable(true);
                    }
                } else {
                    if (UserFilePath != Program.RoamingProfileOGCS) {
                        log.Info("File storage location is incorrect according to " + Settings.ConfigFile);
                        MakePortable(false);
                    }
                }
            }

            string logLevel = XMLManager.ImportElement("LoggingLevel", Settings.ConfigFile);
            Settings.configureLoggingLevel(logLevel ?? "FINE");

            if (args.Contains("--delay")) { //Format up to and including v2.7.1
                log.Info("Converting old --delay parameter to /d");
                try {
                    String delay = args[Array.IndexOf(args, "--delay") + 1];
                    log.Debug("Delay of " + delay + "s being migrated.");
                    addRegKey(delay);
                    delayStartup(delay);
                } catch (System.Exception ex) {
                    log.Error(ex.Message);
                }
            }
            Dictionary<String, String> delayArg = parseArgument(args, 'd');
            if (delayArg["Value"] != null) delayStartup(delayArg["Value"]);

            Dictionary<String, String> titleArg = parseArgument(args, 't');
            Title = titleArg["Value"];
        }

        private static Dictionary<String, String> parseArgument(String[] args, char arg) {
            Dictionary<String, String> details = new Dictionary<String, String>();
            details.Add("Value", null);
            details.Add("Directory", null);
            details.Add("Filename", null);

            try {
                String argVal = args.Where(a => a.ToLower().StartsWith("/" + arg + ":")).FirstOrDefault();
                if (argVal != null) {
                    details["Value"] = argVal.Split(':')[1];
                    if (arg == 'l' || arg == 's') {
                        details["Filename"] = System.IO.Path.GetFileName(argVal);
                        if (string.IsNullOrEmpty(details["Filename"]) || !Path.HasExtension(details["Filename"])) {
                            throw new ApplicationException("The /" + arg + " parameter must be used with a filename.");
                        }
                        details["Directory"] = System.IO.Path.GetDirectoryName(argVal.TrimStart(("/" + arg + ":").ToCharArray()));
                    }
                }
            } catch (System.Exception ex) {
                throw new ApplicationException("Failed processing /" + arg + " parameter. " + ex.Message);
            }
            return details;
        }

        private static void initialiseLogger(string logFilename, string logPath = null, Boolean bootstrap = false) {
            if (string.IsNullOrEmpty(logFilename)) logFilename = defaultLogFilename;
            log4net.GlobalContext.Properties["LogFilename"] = logFilename;
            if (string.IsNullOrEmpty(logPath)) {
                if (Program.IsInstalled || File.Exists(Path.Combine(RoamingProfileOGCS, logFilename)))
                    logPath = RoamingProfileOGCS;
                else
                    logPath = Application.StartupPath;
            }
            UserFilePath = logPath;
            log4net.GlobalContext.Properties["LogPath"] = logPath + "\\";
            log4net.LogManager.GetRepository().LevelMap.Add(MyFailLevel);
            log4net.LogManager.GetRepository().LevelMap.Add(MyFineLevel);
            log4net.LogManager.GetRepository().LevelMap.Add(MyUltraFineLevel);

            GoogleOgcs.ErrorReporting.LogId = "v" + Application.ProductVersion;
            GoogleOgcs.ErrorReporting.UpdateLogUuId();

            XmlConfigurator.Configure(new System.IO.FileInfo(
                Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), logSettingsFile)
            ));

            GoogleOgcs.ErrorReporting.SetThreshold(false);

            if (bootstrap) {
                log.Info("Program started: v" + Application.ProductVersion);
                log.Info("Started " + (isCLIstartup() ? "automatically" : "interactively") + ".");
                if (Environment.GetCommandLineArgs().Count() > 1)
                    log.Info("Invoked with arguments: "+ string.Join(" ", Environment.GetCommandLineArgs().Skip(1).ToArray()));
            }
            log.Info("Logging to: " + logPath + "\\" + logFilename);
            purgeLogFiles(30);
        }

        private static void purgeLogFiles(Int16 retention) {
            log.Info("Purging log files older than "+ retention +" days...");
            foreach (String file in System.IO.Directory.GetFiles(UserFilePath, "*.log.????-??-??", SearchOption.TopDirectoryOnly)) {
                if (System.IO.File.GetLastWriteTime(file) < DateTime.Now.AddDays(-retention)) {
                    log.Debug("Deleted "+ file);
                    System.IO.File.Delete(file);
                }
            }
            log.Info("Purge complete.");
        }

        #region Application Behaviour
        #region Startup Registry Key
        private static String startupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void ManageStartupRegKey(Boolean recreate = false) {
            //Check for legacy Startup menu shortcut <=v2.1.4
            Boolean startupConfigExists = Program.CheckShortcut(Environment.SpecialFolder.Startup);
            if (startupConfigExists) 
                Program.RemoveShortcut(Environment.SpecialFolder.Startup);

            startupConfigExists = checkRegKey();
            
            if (Settings.Instance.StartOnStartup && !startupConfigExists)
                addRegKey();
            else if (!Settings.Instance.StartOnStartup && startupConfigExists)
                removeRegKey();
            else if (startupConfigExists && recreate) {
                log.Debug("Forcing update of startup registry key.");
                addRegKey();
            }
        }

        private static Boolean checkRegKey() {
            String[] regKeys = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupKeyPath).GetValueNames();
            return regKeys.Contains(Application.ProductName);
        }

        private static void addRegKey(String startupDelay = null) {
            Microsoft.Win32.RegistryKey startupKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupKeyPath, true);
            String keyValue = startupKey.GetValue(Application.ProductName, "").ToString();
            String delayedStartup = "";
            if (Convert.ToInt16(startupDelay ?? Settings.Instance.StartupDelay.ToString()) > 0)
                delayedStartup = " /d:" + (startupDelay ?? Settings.Instance.StartupDelay.ToString());

            String cliArgs = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Where(a => "l,s".Contains(a.Substring(1, 1).ToLower())));
            cliArgs = (" " + cliArgs).TrimEnd();

            if (keyValue == "" || keyValue != (Application.ExecutablePath + delayedStartup + cliArgs)) {
                log.Debug("Startup registry key "+ (keyValue == "" ? "created" : "updated") +".");
                try {
                    startupKey.SetValue(Application.ProductName, Application.ExecutablePath + delayedStartup + cliArgs);
                } catch (System.UnauthorizedAccessException ex) {
                    log.Warn("Could not create/update registry key. " + ex.Message);
                    Settings.Instance.StartOnStartup = false;
                    if (OgcsMessageBox.Show("You don't have permission to update the registry, so the application can't be set to run on startup.\r\n" +
                        "Try manually adding a shortcut to the 'Startup' folder in Windows instead?", "Permission denied", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation)
                        == DialogResult.Yes) {
                        System.Diagnostics.Process.Start(System.Windows.Forms.Application.StartupPath);
                        System.Diagnostics.Process.Start(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
                    }
                }
            }
            startupKey.Close();
        }

        private static void removeRegKey() {
            log.Debug("Startup registry key being removed.");
            Microsoft.Win32.RegistryKey startupKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupKeyPath, true);
            startupKey.DeleteValue(Application.ProductName, false);
        }
        #endregion
        private static void delayStartup(String seconds) {
            try {
                DateTime delayUntil = DateTime.Now.AddSeconds(Convert.ToInt32(seconds));
                log.Info("Startup delay configured until " + delayUntil.ToString("HH:mm:ss"));
                while (DateTime.Now < delayUntil) {
                    System.Threading.Thread.Sleep(250);
                }
            } catch (System.Exception ex) {
                log.Warn("Failure in delayStartup(). Seconds: " + seconds);
                log.Error(ex.Message);
            }
        }

        #region Legacy Start Menu Shortcut
        public static Boolean CheckShortcut(Environment.SpecialFolder directory, String subdir = "") {
            log.Debug("CheckShortcut: directory=" + directory.ToString() + "; subdir=" + subdir);
            Boolean foundShortcut = false;
            if (subdir != "") subdir = "\\" + subdir;
            String shortcutDir = Environment.GetFolderPath(directory) + subdir;

            if (!System.IO.Directory.Exists(shortcutDir)) return false;

            foreach (String file in System.IO.Directory.GetFiles(shortcutDir)) {
                if (file.EndsWith("\\OutlookGoogleCalendarSync.lnk") || //legacy name <=v2.1.0.0
                    file.EndsWith("\\" + Application.ProductName + ".lnk")) {
                    foundShortcut = true;
                    break;
                }
            }
            return foundShortcut;
        }

        public static void RemoveShortcut(Environment.SpecialFolder directory, String subdir = "") {
            try {
                log.Debug("RemoveShortcut: directory=" + directory.ToString() + "; subdir=" + subdir);
                if (subdir != "") subdir = "\\" + subdir;
                String shortcutDir = Environment.GetFolderPath(directory) + subdir;

                if (!System.IO.Directory.Exists(shortcutDir)) {
                    log.Info("Failed to delete shortcut in \"" + shortcutDir + "\" - directory does not exist.");
                    return;
                }
                foreach (String file in System.IO.Directory.GetFiles(shortcutDir)) {
                    if (file.EndsWith("\\OutlookGoogleCalendarSync.lnk") || //legacy name <=v2.1.0.0
                        file.EndsWith("\\" + Application.ProductName + ".lnk")) {
                        System.IO.File.Delete(file);
                        log.Info("Deleted shortcut in \"" + shortcutDir + "\"");
                        break;
                    }
                }
            } catch (System.Exception ex) {
                log.Warn("Problem trying to remove legacy Start Menu shortcut.");
                log.Error(ex.Message);
            }
        }
        #endregion

        public static void MakePortable(Boolean portable) {
            if (StartedWithFileArgs) {
                log.Warn("Cannot move user files when OGCS is started with CLI arguments.");
                return;
            }

            if (portable) {
                log.Info("Making the application portable...");
                string appFilePath = System.Windows.Forms.Application.StartupPath;
                if (appFilePath == UserFilePath) {
                    log.Info("It already is!");
                    return;
                }
                moveFiles(UserFilePath, appFilePath);

            } else {
                log.Info("Making the application non-portable...");
                if (RoamingProfileOGCS == UserFilePath) {
                    log.Info("It already is!");
                    return;
                }
                if (!Directory.Exists(RoamingProfileOGCS))
                    Directory.CreateDirectory(RoamingProfileOGCS);

                moveFiles(UserFilePath, RoamingProfileOGCS);
            }
        }

        private static void moveFiles(string srcDir, string dstDir) {
            log.Info("Moving files from " + srcDir + " to " + dstDir + ":-");
            if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

            string dstFile = Path.Combine(dstDir, Settings.ConfigFilename);
            File.Delete(dstFile);
            log.Debug("  " + Settings.ConfigFilename);
            File.Move(Settings.ConfigFile, dstFile);
            WorkingFilesDirectory = dstDir;

            foreach (string file in Directory.GetFiles(srcDir)) {
                if (Path.GetFileName(file).StartsWith("OGcalsync.log") || file.EndsWith(".csv") || file.EndsWith(".json") || file == GoogleOgcs.Authenticator.TokenFile) {
                    dstFile = Path.Combine(dstDir, Path.GetFileName(file));
                    File.Delete(dstFile);
                    log.Debug("  " + Path.GetFileName(file));
                    if (file.EndsWith(".log")) {
                        log.Logger.Repository.Shutdown();
                        log4net.LogManager.Shutdown();
                        LogManager.GetRepository().ResetConfiguration();
                        File.Move(file, dstFile);
                        initialiseLogger(dstDir);
                    } else {
                        File.Move(file, dstFile);
                    }
                }
            }
            try {
                log.Debug("Deleting directory " + srcDir);
                Directory.Delete(srcDir);
            } catch (System.Exception ex) {
                log.Debug(ex.Message);
            }
            UserFilePath = dstDir;
        }
        #endregion

        private static void isNewVersion(Boolean isSquirrelInstall) {
            string settingsVersion = string.IsNullOrEmpty(Settings.Instance.Version) ? "Unknown" : Settings.Instance.Version;
            if (settingsVersion != Application.ProductVersion) {
                log.Info("New version detected - upgraded from " + settingsVersion + " to " + Application.ProductVersion);
                try {
                    Program.ManageStartupRegKey(recreate: true);
                } catch (System.Exception ex) {
                    if (ex is System.Security.SecurityException) OGCSexception.LogAsFail(ref ex); //User doesn't have rights to access registry
                    OGCSexception.Analyse("Failed accessing registry for startup key.", ex);
                }
                Settings.Instance.Version = Application.ProductVersion;
                if (Application.ProductVersion.EndsWith(".0")) { //Release notes not updated for hotfixes.
                    System.Diagnostics.Process.Start("https://github.com/phw198/OutlookGoogleCalendarSync/blob/master/docs/Release%20Notes.md");
                    if (isSquirrelInstall) Telemetry.Send(Analytics.Category.squirrel, Analytics.Action.upgrade, "from=" + settingsVersion + ";to=" + Application.ProductVersion);
                }
            }

            //Check upgrade to Squirrel release went OK
            try {
                if (isSquirrelInstall) {
                    Int32 upgradedFrom = Int16.MaxValue;
                    String expectedInstallDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    expectedInstallDir = Path.Combine(expectedInstallDir, "OutlookGoogleCalendarSync");
                    String paddedVersion = "";
                    if (settingsVersion != "Unknown") {
                        foreach (String versionBit in settingsVersion.Split('.')) {
                            paddedVersion += versionBit.PadLeft(2, '0');
                        }
                        upgradedFrom = Convert.ToInt32(paddedVersion);

                    }
                    if ((settingsVersion == "Unknown" || upgradedFrom < 2050000) &&
                        !System.Windows.Forms.Application.ExecutablePath.ToString().StartsWith(expectedInstallDir)) 
                    {
                        log.Warn("OGCS is running from " + System.Windows.Forms.Application.ExecutablePath.ToString());
                        OgcsMessageBox.Show("A suspected improper install location has been detected.\r\n" +
                            "Click 'OK' for further details.", "Improper Install Location",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        System.Diagnostics.Process.Start("https://github.com/phw198/OutlookGoogleCalendarSync/issues/265");
                    }
                }
            } catch (System.Exception ex) {
                log.Warn("Failed to determine if OGCS is installed in the correct location.");
                log.Error(ex.Message);
            }
        }

        private static Boolean isCLIstartup() {
            try {
                if (File.Exists(logSettingsFile)) return false;
                else if (File.Exists(Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), logSettingsFile))) return true;
                else return false;
            } catch (System.Exception ex) {
                log.Error("Failed to determine if OGCS was started by CLI.");
                OGCSexception.Analyse(ex);
                return false;
            }
        }

        public static void Donate() {
            System.Diagnostics.Process.Start("https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=44DUQ7UT6WE2C&item_name=Outlook Google Calendar Sync from " + Settings.Instance.GaccountEmail);
        }

        public static Boolean InDeveloperMode {
            get { return System.Diagnostics.Debugger.IsAttached; }
        }
    }
}
