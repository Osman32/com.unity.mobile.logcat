using System.Collections.Generic;
using System;
using System.Text.RegularExpressions;
using System.Linq;
using UnityEngine;
using System.Text;
using UnityEditor;

namespace Unity.Android.Logcat
{
    internal class AndroidLogcatInternalLog : EditorWindow
    {
        static AndroidLogcatInternalLog ms_Instance = null;
        static StringBuilder ms_LogEntries = new StringBuilder();

        Vector2 m_ScrollPosition = Vector2.zero;
        public static void ShowLog(bool immediate)
        {
            if (ms_Instance == null)
                ms_Instance = ScriptableObject.CreateInstance<AndroidLogcatInternalLog>();

            ms_Instance.titleContent = new GUIContent("Internal Log");
            ms_Instance.Show(immediate);
            ms_Instance.Focus();
        }

        public static void Log(string message, params object[] args)
        {
            var timedMessage = DateTime.Now.ToString("HH:mm:ss.ffff") + " " + string.Format(message, args);
            ms_LogEntries.AppendLine(timedMessage);

            Console.WriteLine("[Logcat] " + timedMessage);

            if (ms_Instance != null)
            {
                ms_Instance.m_ScrollPosition = new Vector2(ms_Instance.m_ScrollPosition.x, float.MaxValue);
                ms_Instance.Repaint();
            }
        }

        public void OnEnable()
        {
            ms_Instance = this;
        }

        public void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1)
            {
                var menuItems = new[] { new GUIContent("Copy All") };
                EditorUtility.DisplayCustomMenu(new Rect(e.mousePosition.x, e.mousePosition.y, 0, 0), menuItems.ToArray(), -1, MenuSelection, null);
            }

            m_ScrollPosition = GUILayout.BeginScrollView(m_ScrollPosition, true, true);
            GUILayout.TextArea(ms_LogEntries.ToString(), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
        }

        private void MenuSelection(object userData, string[] options, int selected)
        {
            switch (selected)
            {
                // Copy All
                case 0:
                    EditorGUIUtility.systemCopyBuffer = ms_LogEntries.ToString();
                    break;
            }
        }
    }
}