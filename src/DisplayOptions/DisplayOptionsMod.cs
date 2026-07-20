using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DisplayOptions
{
    public class DisplayOptionsSettings : ModSettings
    {
        public FullScreenMode mode = Screen.fullScreenMode;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mode, "displayMode", Screen.fullScreenMode);
        }
    }

    public class DisplayOptionsMod : Mod
    {
        public static DisplayOptionsSettings Settings;

        public DisplayOptionsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DisplayOptionsSettings>();
            WindowMode.Apply(Settings.mode);
            // Mod construction runs before the window has OS focus, and exclusive
            // fullscreen can't be negotiated with the OS until then. Reapply once
            // the boot loading screen finishes and focus is available.
            LongEventHandler.ExecuteWhenFinished(() => WindowModeRunner.Settle(WindowMode.WaitForFocusThenApply(Settings.mode)));
            new Harmony("fincho.displayoptions").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    // Toggling dev mode rebuilds part of the UI and can drop the window back to
    // borderless; reassert our setting whenever it actually flips.
    [HarmonyPatch(typeof(Prefs), "DevMode", MethodType.Setter)]
    public static class DevModeTogglePatch
    {
        private static bool lastValue = Prefs.DevMode;

        public static void Postfix(bool value)
        {
            if (value == lastValue) return;
            lastValue = value;
            WindowMode.Apply(DisplayOptionsMod.Settings.mode);
        }
    }

    // Replaces the vanilla Fullscreen checkbox + Borderless fullscreen button in Options > Video
    [HarmonyPatch(typeof(Dialog_Options), "DoVideoOptions")]
    public static class VideoOptionsPatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();

            MethodInfo borderlessGetter = AccessTools.PropertyGetter(typeof(ResolutionUtility), "BorderlessFullscreen");
            int start = list.FindIndex(ci => ci.Calls(borderlessGetter));

            int textureCompression = list.FindIndex(ci => ci.opcode == OpCodes.Ldstr && "TextureCompression".Equals(ci.operand));
            int end = -1;
            for (int i = textureCompression; i >= start; i--)
            {
                if (list[i].opcode == OpCodes.Newobj) { end = i; break; }
            }

            if (start < 0 || end < 0)
            {
                Log.Error("DisplayOptions: couldn't find the vanilla fullscreen controls; Options > Video is unpatched.");
                return list;
            }

            CodeInstruction pushListing = new CodeInstruction(OpCodes.Ldarg_1);
            pushListing.labels.AddRange(list[start].labels);
            CodeInstruction callDraw = new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(VideoOptionsPatch), "DrawWindowModeSection"));

            list.RemoveRange(start, end - start);
            list.InsertRange(start, new[] { pushListing, callDraw });
            return list;
        }

        public static void DrawWindowModeSection(Listing_Standard listing)
        {
            FullScreenMode current = DisplayOptionsMod.Settings.mode;

            if (listing.RadioButton("Windowed", current == FullScreenMode.Windowed))
                SetMode(FullScreenMode.Windowed);
            if (listing.RadioButton("Borderless", current == FullScreenMode.FullScreenWindow))
                SetMode(FullScreenMode.FullScreenWindow);
            if (listing.RadioButton("Exclusive fullscreen", current == FullScreenMode.ExclusiveFullScreen))
                SetMode(FullScreenMode.ExclusiveFullScreen);
        }

        private static void SetMode(FullScreenMode mode)
        {
            DisplayOptionsMod.Settings.mode = mode;
            WindowMode.Apply(mode);
            DisplayOptionsMod.Settings.Write();
        }
    }

    // Applies the chosen window mode.
    internal static class WindowMode
    {
        private const int GWL_STYLE = -16;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static IntPtr Hwnd()
        {
            IntPtr h = Process.GetCurrentProcess().MainWindowHandle;
            return h != IntPtr.Zero ? h : GetActiveWindow();
        }

        // Style alone isn't enough - a stale window rect throws off Windows' mouse-
        // to-client mapping under exclusive fullscreen, so always set both together.
        private static void ApplyWindowRect(uint style, int x, int y, int width, int height)
        {
            IntPtr hwnd = Hwnd();
            if (hwnd == IntPtr.Zero) return;
            SetWindowLong(hwnd, GWL_STYLE, unchecked((int)(style | WS_VISIBLE)));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        public static void Apply(FullScreenMode mode)
        {
            Resolution native = Screen.currentResolution;

            if (mode == FullScreenMode.Windowed)
            {
                int w = Mathf.RoundToInt(native.width * 0.8f);
                int h = Mathf.RoundToInt(native.height * 0.8f);
                int x = (native.width - w) / 2;
                int y = (native.height - h) / 2;
                ApplyWindowRect(WS_OVERLAPPEDWINDOW, x, y, w, h);
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                WindowModeRunner.Settle(ReassertRect(FullScreenMode.Windowed, WS_OVERLAPPEDWINDOW, x, y, w, h));
                return;
            }

            // Borderless (Unity has no native borderless mode, so we drive it as a
            // chrome-less popup) and exclusive fullscreen both need WS_POPUP here.
            // A bordered style never shows a visible title bar under either - the
            // exclusive swapchain owns the whole display regardless - but Windows
            // still offsets mouse-to-client mapping by that invisible border.
            FullScreenMode unityMode = mode == FullScreenMode.ExclusiveFullScreen ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.Windowed;
            Screen.SetResolution(native.width, native.height, unityMode);
            ApplyWindowRect(WS_POPUP, 0, 0, native.width, native.height);
            // Unity's own resize lands a frame or two late and can overwrite a
            // same-frame change, so keep reapplying briefly until it sticks.
            WindowModeRunner.Settle(ReassertRect(unityMode, WS_POPUP, 0, 0, native.width, native.height));
        }

        // Exclusive fullscreen can be silently refused before the window has OS
        // focus (falls back to windowed instead of throwing), so wait for it.
        public static IEnumerator WaitForFocusThenApply(FullScreenMode mode)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!Application.isFocused && Time.realtimeSinceStartup < deadline)
                yield return null;
            Apply(mode);
        }

        private static IEnumerator ReassertRect(FullScreenMode screenMode, uint style, int x, int y, int width, int height)
        {
            for (int i = 0; i < 15; i++)
            {
                yield return null;
                ApplyWindowRect(style, x, y, width, height);
                // The rect can be right while Unity's fullscreen mode still isn't
                // (e.g. a silently downgraded exclusive-fullscreen request).
                if (Screen.fullScreenMode != screenMode)
                    Screen.SetResolution(width, height, screenMode);
            }
        }
    }

    internal class WindowModeRunner : MonoBehaviour
    {
        private static WindowModeRunner instance;

        public static void Settle(IEnumerator routine)
        {
            if (instance == null)
            {
                GameObject go = new GameObject("DisplayOptionsRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                instance = go.AddComponent<WindowModeRunner>();
            }
            instance.StartCoroutine(routine);
        }
    }
}
