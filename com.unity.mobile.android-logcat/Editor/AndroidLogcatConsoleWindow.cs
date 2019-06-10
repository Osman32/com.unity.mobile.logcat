using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
#if PLATFORM_ANDROID
using UnityEditor.Android;
#endif

namespace Unity.Android.Logcat
{
    internal partial class AndroidLogcatConsoleWindow : EditorWindow
#if PLATFORM_ANDROID
        , IHasCustomMenu
    {
        private int m_SelectedDeviceIndex;
        private string m_SelectedDeviceId;
        private string[] m_DeviceDetails = new string[0];
        private List<string> m_DeviceIds = new List<string>();
        private IDictionary<string, AndroidDevice> m_CachedDevices = new Dictionary<string, AndroidDevice>();
        private GUIContent kAutoRunText = new GUIContent(L10n.Tr("Auto Run"), L10n.Tr("Automatically launch logcat window during build & run."));
        private GUIContent kReconnect = new GUIContent(L10n.Tr("Reconnect"), L10n.Tr("Restart logcat process."));
        private GUIContent kRegexText = new GUIContent(L10n.Tr("Regex"), L10n.Tr("Treat contents in search field as regex expression."));
        private GUIContent kClearButtonText = new GUIContent(L10n.Tr("Clear"), L10n.Tr("Clears logcat by executing adb logcat -c."));
        private GUIContent kCaptureScreenText = new GUIContent(L10n.Tr("Capture Screen"), L10n.Tr("Capture the current screen on the device."));

        private const string kJsonFileEditorPrefKey = "AndroidLogcatStateJsonFile";
        private const string kJsonFileName = "AndroidLogcatJsonFile.json";

        private Rect m_IpWindowScreenRect;

        private enum PackageType
        {
            None,
            DefinedFromPlayerSettings,
            TopActivityPackage
        }

        [Serializable]
        internal class PackageInformation
        {
            public string deviceId;
            public string name;
            public string displayName;
            public int processId;
            public bool exited;

            public PackageInformation()
            {
                Reset();
            }

            public void Reset()
            {
                deviceId = string.Empty;
                name = string.Empty;
                displayName = string.Empty;
                processId = 0;
                exited = false;
            }
        }

        private PackageInformation m_SelectedPackage = null;

        private List<PackageInformation> PackagesForSelectedDevice
        {
            get { return GetPackagesForDevice(m_SelectedDeviceId); }
        }

        private List<PackageInformation> GetPackagesForDevice(string deviceId)
        {
            return m_PackagesForAllDevices[deviceId];
        }

        private Dictionary<string, List<PackageInformation>> m_PackagesForAllDevices = new Dictionary<string, List<PackageInformation>>();

        private AndroidLogcat.Priority m_SelectedPriority;

        private string m_Filter = string.Empty;
        private bool m_FilterIsRegularExpression;

        private SearchField m_SearchField;

        private AndroidLogcatTagsControl m_TagControl = null;

        private AndroidLogcatJsonSerialization m_JsonSerialization = null;

        private AndroidLogcat m_LogCat;
        private AndroidLogcatStatusBar m_StatusBar;
        private ADB m_Adb;
        private DateTime m_TimeOfLastDeviceListUpdate;
        private DateTime m_TimeOfLastAutoConnectUpdate;
        private DateTime m_TimeOfLastAutoConnectStart;

        private List<AndroidLogcat.LogEntry> m_LogEntries = new List<AndroidLogcat.LogEntry>();

        private const byte kSpace = 3;
        private const int kMillisecondsBetweenConsecutiveDeviceChecks = 1000;
        private const int kMillisecondsBetweenConsecutiveAutoConnectChecks = 1000;
        private const int kMillisecondsMaxAutoconnectTimeOut = 5000;

        private bool m_AutoSelectPackage;
        private bool m_FinishedAutoselectingPackage;

        private static string kAutoShowLogcatDuringBuildRun = "AutoShowLogcatDuringBuildRun";

        public bool AutoSelectPackage
        {
            set
            {
                m_AutoSelectPackage = value;
                m_FinishedAutoselectingPackage = false;
                if (m_StatusBar != null && m_AutoSelectPackage)
                    m_StatusBar.Message = "Waiting for '" + PlayerSettings.applicationIdentifier + "'";
            }

            get
            {
                return m_AutoSelectPackage;
            }
        }

        void OnDestroy()
        {
            SaveStates();

            if (m_TagControl.TagWindow != null)
                m_TagControl.TagWindow.Close();
        }

        internal void SaveStates()
        {
            m_JsonSerialization = new AndroidLogcatJsonSerialization();
            m_JsonSerialization.m_SelectedDeviceId = m_SelectedDeviceId;
            m_JsonSerialization.m_SelectedPackage = m_SelectedPackage;
            m_JsonSerialization.m_SelectedPriority = m_SelectedPriority;
            m_JsonSerialization.m_TagControl = m_TagControl;

            // Convert Dictionary to List for serialization.
            var packagesForSerialization = new List<PackageInformation>();
            foreach (var p in m_PackagesForAllDevices)
            {
                packagesForSerialization.AddRange(p.Value);
            }
            m_JsonSerialization.m_PackagesForSerialization = packagesForSerialization;

            var jsonString = JsonUtility.ToJson(m_JsonSerialization);
            m_JsonSerialization = null;
            if (string.IsNullOrEmpty(jsonString))
                return;

            var jsonFilePath = Path.Combine(Application.persistentDataPath, kJsonFileName);
            if (File.Exists(jsonFilePath))
                File.Delete(jsonFilePath);
            File.WriteAllText(jsonFilePath, jsonString);

            EditorPrefs.SetString(kJsonFileEditorPrefKey, jsonFilePath);
        }

        internal void LoadStates()
        {
            if (!EditorPrefs.HasKey(kJsonFileEditorPrefKey))
                return;

            var jsonFile = EditorPrefs.GetString(kJsonFileEditorPrefKey);
            if (string.IsNullOrEmpty(jsonFile) || !File.Exists(jsonFile))
                return;

            var jsonString = File.ReadAllText(jsonFile);
            if (string.IsNullOrEmpty(jsonString))
                return;

            try
            {
                m_JsonSerialization = JsonUtility.FromJson<AndroidLogcatJsonSerialization>(jsonString);
            }
            catch (Exception ex)
            {
                AndroidLogcatInternalLog.Log("Load Preferences from Json failed: " + ex.Message);
                m_JsonSerialization = null;
                return;
            }

            // We can only restore Priority, TagControl & PackageForSerialization here.
            // For selected device & package, we have to delay it when we first launch the window.
            m_SelectedPriority = m_JsonSerialization.m_SelectedPriority;
            m_TagControl.TagNames = m_JsonSerialization.m_TagControl.TagNames;
            m_TagControl.SelectedTags = m_JsonSerialization.m_TagControl.SelectedTags;

            m_PackagesForAllDevices = new Dictionary<string, List<PackageInformation>>();
            foreach (var p in m_JsonSerialization.m_PackagesForSerialization)
            {
                List<PackageInformation> packages;
                if (!m_PackagesForAllDevices.TryGetValue(p.deviceId, out packages))
                {
                    packages = new List<PackageInformation>();
                    m_PackagesForAllDevices[p.deviceId] = packages;
                }
                packages.Add(p);
            }
        }

        private void OnEnable()
        {
            AndroidLogcatInternalLog.Log("OnEnable");

            if (m_SearchField == null)
                m_SearchField = new SearchField();

            if (m_TagControl == null)
                m_TagControl = new AndroidLogcatTagsControl();
            m_TagControl.TagSelectionChanged += TagSelectionChanged;

            m_SelectedDeviceIndex = -1;
            m_SelectedDeviceId = null;

            m_TimeOfLastAutoConnectStart = DateTime.Now;
            EditorApplication.update += Update;

            m_FinishedAutoselectingPackage = false;
            AndroidLogcatInternalLog.Log("Package: {0}, Auto select: {1}", PlayerSettings.applicationIdentifier, m_AutoSelectPackage);

            m_StatusBar = new AndroidLogcatStatusBar();
            if (m_AutoSelectPackage)
                m_StatusBar.Message = "Waiting for '" + PlayerSettings.applicationIdentifier + "'";
        }

        private void OnDisable()
        {
            if (m_TagControl.TagWindow != null)
            {
                m_TagControl.TagWindow.Close();
                m_TagControl.TagWindow = null;
            }

            StopLogCat();
            EditorApplication.update -= Update;
            AndroidLogcatInternalLog.Log("OnDisable, Auto select: {0}", m_AutoSelectPackage);
        }

        private void RemoveTag(string tag)
        {
            if (!m_TagControl.Remove(tag))
                return;

            RestartLogCat();
        }

        private void AddTag(string tag)
        {
            if (!m_TagControl.Add(tag, true))
                return;

            RestartLogCat();
        }

        private void TagSelectionChanged()
        {
            RestartLogCat();
        }

        private void Update()
        {
            if (m_DeviceIds != null && m_DeviceIds.Count == 0)
                UpdateConnectedDevicesList(false);

            if (m_DeviceIds.Count == 0)
                return;

            if (m_AutoSelectPackage && !m_FinishedAutoselectingPackage)
            {
                // This is for AutoRun triggered by "Build And Run".
                if ((DateTime.Now - m_TimeOfLastAutoConnectUpdate).TotalMilliseconds < kMillisecondsBetweenConsecutiveAutoConnectChecks)
                    return;
                m_TimeOfLastAutoConnectUpdate = DateTime.Now;

                ResetPackages(m_DeviceIds[0]);

                int projectApplicationPid = GetPidFromPackageName(PlayerSettings.applicationIdentifier, m_DeviceIds[0]);
                var package = CreatePackageInformation(PlayerSettings.applicationIdentifier, projectApplicationPid, m_DeviceIds[0]);
                if (package != null)
                {
                    // Note: Don't call SelectPackage as that will reset m_AutoselectPackage
                    m_SelectedPackage = package;
                    RestartLogCat();
                    m_FinishedAutoselectingPackage = true;
                    UpdateStatusBar();
                }
                else
                {
                    var timeoutMS = (DateTime.Now - m_TimeOfLastAutoConnectStart).TotalMilliseconds;
                    if (timeoutMS > kMillisecondsMaxAutoconnectTimeOut)
                    {
                        var msg = string.Format("Timeout {0} ms while waiting for '{1}' to launch.", timeoutMS, PlayerSettings.applicationIdentifier);
                        UpdateStatusBar(msg);
                        AndroidLogcatInternalLog.Log(msg);
                        m_FinishedAutoselectingPackage = true;
                    }
                }
            }
            else
            {
                if (m_SelectedDeviceId == null)
                {
                    int selectedDeviceIndex;
                    PackageInformation selectedPackage;
                    GetSelectedDeviceIndex(out selectedDeviceIndex, out selectedPackage);
                    SetSelectedDeviceByIndex(selectedDeviceIndex, true);
                    SelectPackage(selectedPackage);
                }
            }
        }

        private void GetSelectedDeviceIndex(out int selectedDeviceIndex, out PackageInformation selectedPackage)
        {
            if (m_JsonSerialization == null || string.IsNullOrEmpty(m_JsonSerialization.m_SelectedDeviceId) || m_DeviceIds.IndexOf(m_JsonSerialization.m_SelectedDeviceId) < 0)
            {
                selectedDeviceIndex = 0;
                selectedPackage = null;
                m_JsonSerialization = null;
                return;
            }

            selectedDeviceIndex = m_DeviceIds.IndexOf(m_JsonSerialization.m_SelectedDeviceId);
            selectedPackage = m_JsonSerialization.m_SelectedPackage;

            // We should only restore from AndroidLogcatJsonSerialization once during first launching.
            m_JsonSerialization = null;
        }

        private void OnDeviceDisconnected(string deviceId)
        {
            StopLogCat();
            var msg = "Either adb.exe crashed or device disconnected (device id: " + GetDeviceDetailsFor(deviceId) + ")";
            AndroidLogcatInternalLog.Log(msg);
            var index = m_DeviceIds.IndexOf(deviceId);
            if (index == -1)
                return;

            m_DeviceIds.RemoveAt(index);
            ArrayUtility.RemoveAt(ref m_DeviceDetails, index);

            m_SelectedDeviceIndex = -1;
            m_SelectedDeviceId = null;

            if (m_DeviceIds.Count > 0)
                SetSelectedDeviceByIndex(0, true);
            else
                UpdateStatusBar(msg);
        }

        private void OnDeviceConnected(string deviceId)
        {
            UpdateStatusBar(string.Empty);
        }

        private void OnNewLogEntryAdded(List<AndroidLogcat.LogEntry> entries)
        {
            m_LogEntries.AddRange(entries);
            Repaint();
        }

        internal static bool ShowDuringBuildRun
        {
            get
            {
                return EditorPrefs.GetBool(kAutoShowLogcatDuringBuildRun, true);
            }
            set
            {
                EditorPrefs.SetBool(kAutoShowLogcatDuringBuildRun, value);
            }
        }

        internal void OnGUI()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal(AndroidLogcatStyles.toolbar);
            {
                ShowDuringBuildRun = GUILayout.Toggle(ShowDuringBuildRun, kAutoRunText, AndroidLogcatStyles.toolbarButton);

                HandleSelectedDeviceField();

                HandleSelectedPackage();

                HandleSearchField();
                GUILayout.Space(kSpace);

                SetRegex(GUILayout.Toggle(m_FilterIsRegularExpression, kRegexText, AndroidLogcatStyles.toolbarButton));

                GUILayout.Space(kSpace);

                if (GUILayout.Button(kReconnect, AndroidLogcatStyles.toolbarButton))
                    RestartLogCat();

                GUILayout.Space(kSpace);
                if (GUILayout.Button(kClearButtonText, AndroidLogcatStyles.toolbarButton))
                {
                    ClearLogCat();
                    Repaint();
                }

                GUILayout.Space(kSpace);
                if (GUILayout.Button(kCaptureScreenText, AndroidLogcatStyles.toolbarButton))
                {
                    var screenFilePath = AndroidLogcatUtilities.CaptureScreen(GetCachedAdb(), m_SelectedDeviceId);
                    if (!string.IsNullOrEmpty(screenFilePath))
                        AndroidLogcatScreenCaptureWindow.Show(screenFilePath);
                    Repaint();
                }

                GUILayout.Space(kSpace);
                if (GUILayout.Button("Open terminal", AndroidLogcatStyles.toolbarButton))
                {
                    AndroidLogcatUtilities.OpenTerminal(Path.GetDirectoryName(GetCachedAdb().GetADBPath()));
                }

                // Don't erase, used for debugging purposes
                /*
                if (GUILayout.Button("Reload Me", AndroidLogcatStyles.toolbarButton))
                {
                    UnityEditorInternal.InternalEditorUtility.RequestScriptReload();
                }

                if (GUILayout.Button("AutoSelect " + m_AutoSelectPackage.ToString(), AndroidLogcatStyles.toolbarButton))
                {
                    m_AutoSelectPackage = true;
                }

                if (GUILayout.Button("Stop logcat ", AndroidLogcatStyles.toolbarButton))
                {
                    StopLogCat();
                }

                if (GUILayout.Button("Add Log lines", AndroidLogcatStyles.toolbarButton))
                {
                    for (int i = 0; i < 7000; i++)
                        m_LogEntries.Add(new AndroidLogcat.LogEntry() { processId = i, message = "Dummy", tag = "sdsd" });
                    Repaint();
                }

                if (GUILayout.Button("Remove All Log Lines", AndroidLogcatStyles.toolbarButton))
                {
                    m_LogEntries.RemoveAt(0);
                    Repaint();
                }
                // Debugging purposes */
            }
            EditorGUILayout.EndHorizontal();
            if (DoMessageView())
            {
                Repaint();
            }

            if (m_StatusBar != null)
                m_StatusBar.DoGUI();

            EditorGUILayout.EndVertical();
        }

        private void DeviceSelection(object userData, string[] options, int selected)
        {
            if (selected == m_DeviceIds.Count)
            {
                AndroidLogcatIPWindow.Show(this.GetCachedAdb(), m_IpWindowScreenRect);
                return;
            }

            SetSelectedDeviceByIndex(selected);
        }

        private void HandleSelectedDeviceField()
        {
            var currentSelectedDevice = m_SelectedDeviceIndex >= 0 && m_SelectedDeviceIndex < m_DeviceDetails.Length ? m_DeviceDetails[m_SelectedDeviceIndex] : "No device";
            GUILayout.Label(new GUIContent(currentSelectedDevice, "Select android device"), AndroidLogcatStyles.toolbarPopup);
            var rect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                // Only update device list, when we select this UI item
                UpdateConnectedDevicesList(true);

                var names = m_DeviceDetails.Select(m => new GUIContent(m)).ToList();
                // Add <Enter IP> as last field to let user connect through wifi.
                names.Add(new GUIContent("<Enter IP>"));

                // Store the screen-space place that we should show the AndroidLogcatIPWindow.
                m_IpWindowScreenRect = GUIUtility.GUIToScreenRect(rect);

                EditorUtility.DisplayCustomMenu(new Rect(rect.x, rect.yMax, 0, 0), names.ToArray(), CheckDeviceEnabled, m_SelectedDeviceIndex, DeviceSelection, null);
            }

            GUILayout.Space(kSpace);
        }

        private bool CheckDeviceEnabled(int index)
        {
            return true;
        }

        private void SelectPackage(PackageInformation newPackage)
        {
            if ((m_SelectedPackage == null && newPackage == null) ||
                (newPackage != null && m_SelectedPackage != null && newPackage.name == m_SelectedPackage.name && newPackage.processId == m_SelectedPackage.processId))
                return;

            m_AutoSelectPackage = false;
            m_SelectedPackage = newPackage;

            RestartLogCat();
        }

        private void PackageSelection(object userData, string[] options, int selected)
        {
            PackageInformation[] packages = (PackageInformation[])userData;
            SelectPackage(packages[selected]);
        }

        private void ResetPackages(string deviceId)
        {
            m_SelectedPackage = null;
            List<PackageInformation> packages;
            if (!m_PackagesForAllDevices.TryGetValue(deviceId, out packages))
            {
                packages = new List<PackageInformation>();
                m_PackagesForAllDevices.Add(deviceId, packages);
            }
        }

        private void HandleSelectedPackage()
        {
            // We always keep track the list of following packages:
            // * No Filter
            // * Package defined from player settings
            // * Package which is from top activity on phone and if it's not the one from player settings
            var displayName = m_SelectedPackage != null && m_SelectedPackage.processId != 0 ? m_SelectedPackage.displayName : "No Filter";
            GUILayout.Label(new GUIContent(displayName, "Select package name"), AndroidLogcatStyles.toolbarPopup);
            var rect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                if (string.IsNullOrEmpty(m_SelectedDeviceId))
                    return;

                UpdateDebuggablePackages();

                List<PackageInformation> packages = new List<PackageInformation>(PackagesForSelectedDevice);

                var appName = PlayerSettings.applicationIdentifier;
                packages.Sort(delegate(PackageInformation x, PackageInformation y)
                {
                    if (x.name == appName && !x.exited)
                        return -1;
                    if (y.name == appName && !y.exited)
                        return 1;
                    if (x.exited && !y.exited)
                        return 1;
                    if (!x.exited && y.exited)
                        return -1;
                    return 0;
                });

                // Add No Filter "package"
                packages.Insert(0, null);

                var names = new GUIContent[packages.Count];
                int selectedPackagedId = m_SelectedPackage == null || m_SelectedPackage.processId == 0 ? 0 : -1;
                for (int i = 0; i < packages.Count; i++)
                {
                    names[i] = new GUIContent(packages[i] == null ? "No Filter" : packages[i].displayName);

                    if (packages[i] != null && m_SelectedPackage != null && m_SelectedPackage.name == packages[i].name && m_SelectedPackage.processId == packages[i].processId)
                        selectedPackagedId = i;
                }

                EditorUtility.DisplayCustomMenu(
                    new Rect(rect.x, rect.yMax, 0, 0),
                    names,
                    selectedPackagedId,
                    PackageSelection, packages.ToArray());
            }

            GUILayout.Space(kSpace);
        }

        private void HandleSearchField()
        {
            var newFilter = m_SearchField.OnToolbarGUI(m_Filter, null);
            SetFilter(newFilter);
            m_SearchField.SetFocus();
        }

        private void SetSelectedDeviceByIndex(int newDeviceIndex, bool force = false)
        {
            if (newDeviceIndex != m_SelectedDeviceIndex || force)
            {
                m_SelectedDeviceIndex = newDeviceIndex;
                m_SelectedDeviceId = m_DeviceIds[m_SelectedDeviceIndex];
                ResetPackages(m_SelectedDeviceId);
                UpdateDebuggablePackages();
                RestartLogCat();
            }
        }

        private void SetSelectedPriority(AndroidLogcat.Priority newPriority)
        {
            if (newPriority != m_SelectedPriority)
            {
                m_SelectedPriority = newPriority;
                RestartLogCat();
            }
        }

        private void SetFilter(string newFilter)
        {
            if (newFilter == m_Filter)
                return;

            m_Filter = string.IsNullOrEmpty(newFilter) ? string.Empty : newFilter;
            RestartLogCat();
        }

        private void SetRegex(bool newValue)
        {
            if (newValue == m_FilterIsRegularExpression)
                return;

            m_FilterIsRegularExpression = newValue;
            RestartLogCat();
        }

        private void ConnectToDeviceId(string deviceId)
        {
            if (deviceId == null)
                return;

            var adb = GetCachedAdb();
            var device = GetAndroidDeviceFromCache(adb, deviceId);

            m_LogCat = new AndroidLogcat(adb, device, m_SelectedPackage == null ? 0 : m_SelectedPackage.processId, m_SelectedPriority, m_Filter, m_FilterIsRegularExpression, m_TagControl.GetSelectedTags());
            m_LogCat.LogEntriesAdded += OnNewLogEntryAdded;
            m_LogCat.DeviceDisconnected += OnDeviceDisconnected;
            m_LogCat.DeviceConnected += OnDeviceConnected;

            m_LogCat.Start();
        }

        private void UpdateConnectedDevicesList(bool immediate)
        {
            if ((DateTime.Now - m_TimeOfLastDeviceListUpdate).TotalMilliseconds < kMillisecondsBetweenConsecutiveDeviceChecks && !immediate)
                return;
            m_TimeOfLastDeviceListUpdate = DateTime.Now;

            var adb = GetCachedAdb();

            m_DeviceIds = AndroidLogcatUtilities.RetrieveConnectedDeviceIds(adb);

            // Ensure selected device does not change (due to a new device name taking the same index)
            if (m_SelectedDeviceId != null)
            {
                m_SelectedDeviceIndex = m_DeviceIds.IndexOf(m_SelectedDeviceId);
            }

            var devicesDetails = new List<string>();
            foreach (var deviceId in m_DeviceIds)
            {
                devicesDetails.Add(AndroidLogcatUtilities.RetrieveDeviceDetails(GetAndroidDeviceFromCache(adb, deviceId), deviceId));
            }
            m_DeviceDetails = devicesDetails.ToArray();
        }

        private void CheckIfPackagesExited()
        {
            foreach (var package in PackagesForSelectedDevice)
            {
                if (package == null || package.processId <= 0 || package.exited)
                    continue;

                if (GetPidFromPackageName(package.name, m_SelectedDeviceId) != package.processId)
                {
                    package.exited = true;
                    package.displayName += " [Exited]";
                }
            }
        }

        private PackageInformation CreatePackageInformation(string packageName, int pid, string deviceId)
        {
            if (pid <= 0)
                return null;

            var packages = GetPackagesForDevice(deviceId);
            PackageInformation info = packages.FirstOrDefault(package => package.processId == pid);
            if (info != null)
                return info;

            var newPackage = new PackageInformation()
            {
                name = packageName,
                displayName = packageName + " (" + pid + ")",
                processId = pid,
                deviceId = deviceId
            };

            packages.Add(newPackage);
            return newPackage;
        }

        private void UpdateDebuggablePackages()
        {
            CheckIfPackagesExited();

            int topActivityPid = 0;
            string topActivityPackageName = string.Empty;
            bool checkProjectPackage = true;
            if (AndroidLogcatUtilities.GetTopActivityInfo(GetCachedAdb(), m_SelectedDeviceId, ref topActivityPackageName, ref topActivityPid)
                && topActivityPid > 0)
            {
                CreatePackageInformation(topActivityPackageName, topActivityPid, m_SelectedDeviceId);

                checkProjectPackage = topActivityPackageName != PlayerSettings.applicationIdentifier;
            }

            if (checkProjectPackage)
            {
                int projectApplicationPid = GetPidFromPackageName(PlayerSettings.applicationIdentifier, m_SelectedDeviceId);
                CreatePackageInformation(PlayerSettings.applicationIdentifier, projectApplicationPid, m_SelectedDeviceId);
            }
        }

        private int GetPidFromPackageName(string packageName, string deviceId)
        {
            var adb = GetCachedAdb();
            var device = GetAndroidDeviceFromCache(adb, deviceId);

            return AndroidLogcatUtilities.GetPidFromPackageName(adb, device, deviceId, packageName);
        }

        private string GetDeviceDetailsFor(string deviceId)
        {
            var deviceIndex = m_DeviceIds.IndexOf(deviceId);
            System.Diagnostics.Debug.Assert(deviceIndex >= 0);

            return m_DeviceDetails[deviceIndex];
        }

        private AndroidDevice GetAndroidDeviceFromCache(ADB adb, string deviceId)
        {
            AndroidDevice device;
            if (m_CachedDevices.TryGetValue(deviceId, out device))
            {
                return device;
            }

            try
            {
                device = new AndroidDevice(adb, deviceId);
                m_CachedDevices[deviceId] = device;
            }
            catch (Exception ex)
            {
                AndroidLogcatInternalLog.Log("Exception caugth while trying to retrieve device details for device {0}. This is harmless and device id will be used. Details\r\n:{1}", deviceId, ex);
                // device will be null in this case (and it will not be added to the cache)
            }

            return device;
        }

        private void RestartLogCat()
        {
            StopLogCat();

            m_LogEntries.Clear();

            ConnectToDeviceId(m_SelectedDeviceId);
        }

        private void StopLogCat()
        {
            if (m_LogCat != null)
                m_LogCat.Stop();
            m_LogCat = null;
            UpdateStatusBar();
        }

        private void ClearLogCat()
        {
            if (m_LogCat == null)
            {
                m_LogEntries.Clear();
                m_SelectedIndices.Clear();
                return;
            }

            m_LogCat.Stop();
            m_LogEntries.Clear();
            m_SelectedIndices.Clear();
            m_LogCat.Clear();
            m_LogCat.Start();
        }

        private ADB GetCachedAdb()
        {
            if (m_Adb == null)
                m_Adb = ADB.GetInstance();

            return m_Adb;
        }

        public static void ShowInternalLog()
        {
            AndroidLogcatInternalLog.ShowLog(true);
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(EditorGUIUtility.TrTextContent("Internal Log"), false, ShowInternalLog);
        }

        public void UpdateStatusBar()
        {
            UpdateStatusBar(string.Empty);
        }

        public void UpdateStatusBar(string message)
        {
            if (m_StatusBar == null)
                return;

            m_StatusBar.Connected = m_LogCat != null && m_LogCat.IsConnected;
            m_StatusBar.Message = message;

            Repaint();
        }

#else
    {
        internal void OnGUI()
        {
        #if !PLATFORM_ANDROID
            EditorGUILayout.HelpBox("Please switch active platform to be Android in Build Settings Window.", MessageType.Info);
        #endif
        }

#endif

        [MenuItem("Window/Analysis/Android Logcat &6")]
        internal static AndroidLogcatConsoleWindow ShowWindow()
        {
            return ShowNewOrExisting(false);
        }

        internal static AndroidLogcatConsoleWindow ShowNewOrExisting(bool autoSelectPackage)
        {
            var wnd = GetWindow<AndroidLogcatConsoleWindow>();
            if (wnd == null)
            {
                wnd = ScriptableObject.CreateInstance<AndroidLogcatConsoleWindow>();
            }

            wnd.titleContent = new GUIContent("Android Logcat");
#if PLATFORM_ANDROID
            wnd.AutoSelectPackage = autoSelectPackage;
            wnd.LoadStates();
#endif
            wnd.Show();
            wnd.Focus();

            return wnd;
        }
    }
}
