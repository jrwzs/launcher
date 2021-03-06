/* This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License along
 * with this program; if not, write to the Free Software Foundation, Inc.,
 * 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Launcher.Models;
using Launcher.Utilities;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using Launcher.Utilities.Proxy;

namespace Launcher
{
    public class LineageClient
    {
        //assorted constants needed
        private const int DispChangeSuccessful = 0;
        private const int EnumCurrentSettings = -1;
        private readonly string _processName;
        private static List<LineageClient> _hookedWindows;
        private const int MaxRetries = 10;
        private static Settings _appSettings = null;
        private Socket _clientSocket;
        private Socket _serverSocket;
        private Thread _clientThread;
        private Thread _serverThread;
        private readonly int _serverPort;
        private readonly IPAddress _serverIp;
        private readonly ProxyServer _proxyServer;

        public Process Process { get; private set; }

        private static void MoveWindowCallback(IntPtr hWinEventHook, uint iEvent, IntPtr hWnd, int idObject, int idChild, int dwEventThread, int dwmsEventTime)
        {
            Win32Api.InvalidateRect(IntPtr.Zero, IntPtr.Zero, true);
            Win32Api.RedrawWindow(hWinEventHook, IntPtr.Zero, IntPtr.Zero, Win32Api.RedrawWindowFlags.UpdateNow);
        } //end MoveWindowCallback

        public LineageClient(string settingsKeyName, string processName, string clientDirectory, ProxyServer proxyServer, IPAddress ip,
            int port, List<LineageClient> hookedWindows)
        {
            this._processName = processName;
            _hookedWindows = hookedWindows;
            _appSettings = Helpers.LoadSettings(settingsKeyName);
            this._serverIp = ip;
            this._serverPort = port;
            this._proxyServer = proxyServer;
        } //end constructor

        public static Win32Api.DevMode ChangeDisplayColour(int bitCount)
        {
            return LineageClient.ChangeDisplaySettings(-1, -1, bitCount);
        } //end ChangeDisplayColour

        public static Win32Api.DevMode ChangeDisplaySettings(Win32Api.DevMode mode)
        {
            return LineageClient.ChangeDisplaySettings(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmBitsPerPel);
        } //end ChangeDisplaySettings

        public static List<Resolution> GetResolutions(bool isWin8OrHigher)
        {
            var currentResolution = new Win32Api.DevMode();
            Win32Api.EnumDisplaySettings(null, EnumCurrentSettings, ref currentResolution);

            var returnValue = new List<Resolution>();
            var i = 0;
            var displayDevice = new Win32Api.DevMode();

            // if we are pre Win 8, then use 16 bit colours, otherwise we use 32 bit since 16 bit isn't supported
            var colourBit = isWin8OrHigher ? 32 : 16;

            while (Win32Api.EnumDisplaySettings(null, i, ref displayDevice))
            {
                var colour = displayDevice.dmBitsPerPel;
                var width = displayDevice.dmPelsWidth;
                var height = displayDevice.dmPelsHeight;

                if (colour == colourBit && currentResolution.dmDisplayFrequency == displayDevice.dmDisplayFrequency
                    && displayDevice.dmDisplayFixedOutput == 0 && width >= 800 &&
                    (width != currentResolution.dmPelsWidth && height != currentResolution.dmPelsHeight))
                    returnValue.Add(new Resolution
                    {
                        Width = width,
                        Height = height,
                        Colour = colour
                    });

                i++;
            } //end while

            return returnValue.OrderByDescending(b => b.Width).ThenByDescending(b => b.Height).ToList();
        } //end GetResolutions

        public static Win32Api.DevMode ChangeDisplaySettings(int width, int height, int bitCount)
        {
            var originalMode = new Win32Api.DevMode();
            originalMode.dmSize = (short)Marshal.SizeOf(originalMode);

            // Retrieving current settings
            // to edit them
            Win32Api.EnumDisplaySettings(null, EnumCurrentSettings, ref originalMode);

            // Making a copy of the current settings
            // to allow reseting to the original mode
            var newMode = originalMode;

            // Changing the settings
            if (width != -1 && height != -1)
            {
                newMode.dmPelsHeight = height;
                newMode.dmPelsWidth = width;
            }
            
            newMode.dmBitsPerPel = bitCount;

            // Capturing the operation result
            var result = Win32Api.ChangeDisplaySettings(ref newMode, 0);

            if (result != DispChangeSuccessful)
                MessageBox.Show(@"Resolution change failed. Unable to modify resolution.");

            return originalMode;
        } //end ChangeDisplaySettings

        public void Initialize()
        {
            var attempts = 0;

            while (attempts < MaxRetries)
            {
                var procs = Process.GetProcesses();
                foreach (var proc in procs)
                {
                    if (!proc.ProcessName.StartsWith(this._processName) || 
                        (_hookedWindows.Count > 0 && _hookedWindows.All(b => b.Process.Id == proc.Id)))
                        continue;

                    var pFoundWindow = proc.MainWindowHandle;
                    var procHandleId = pFoundWindow.ToInt32();
                    if (procHandleId > 0)
                    {
                        this.Process = proc;

                        if(_appSettings.Windowed)
                        {
                            Thread.Sleep(_appSettings.WindowedDelay);
                            var style = Win32Api.GetWindowLong(pFoundWindow, (int)(Win32Api.WindowLongFlags.GwlStyle));

                            var hmenu = Win32Api.GetMenu(proc.MainWindowHandle);
                            var count = Win32Api.GetMenuItemCount(hmenu);

                            for (var i = 0; i < count; i++)
                                Win32Api.RemoveMenu(hmenu, 0, (int)(Win32Api.MenuFlags.MfByposition | Win32Api.MenuFlags.MfRemove));

                            //force a redraw
                            Win32Api.DrawMenuBar(proc.MainWindowHandle);
                            Win32Api.SetWindowLong(pFoundWindow, (int)Win32Api.WindowLongFlags.GwlStyle, (style & ~(int)Win32Api.WindowLongFlags.WsSysmenu));
                        }

                        return;
                    } //end if
                } //end foreach

                Thread.Sleep(500);
                attempts++;
            } //end while
        } //end Initialize

        public void Stop()
        {
            if(this._serverThread != null)
                this._serverThread.Abort();

            if(this._clientThread != null)
                this._clientThread.Abort();

            if(this._serverSocket != null)
                this._serverSocket.Close();

            if(this._clientSocket != null)
                this._clientSocket.Close();
        } //end Stop
    } //end class
} //end namespace
