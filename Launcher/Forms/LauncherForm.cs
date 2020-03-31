﻿/* This program is free software; you can redistribute it and/or modify
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
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Launcher.Models;
using Microsoft.Win32;
using System.Reflection;

namespace Launcher.Forms
{
    public partial class LauncherForm : Form
    {
        private const string Version = "5.0.0";

        private readonly LauncherConfig _config;
        private VersionInfo _versionInfo;
        private bool _hasUpdates;

        public LauncherForm()
        {
            var appLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var associatedLaunchers = Helpers.GetAssociatedLaunchers(appLocation);
            
            if (!Helpers.LauncherInLineageDirectory(appLocation))
            {
                MessageBox.Show("The launcher must be installed in your Lineage directory!\n\n " +
                    @"Please reinstall if you used the installer, or move this file to your lineage directory.", @"Invalid Directory", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                this.Close();
                return;
            }

            // If no launchers are available, let's assume resurrection
            if (associatedLaunchers.Count == 0)
                associatedLaunchers.Add("L1J Server");

            if (associatedLaunchers.Count > 1)
                MessageBox.Show("More than one launcher associated with this folder! Using the first one found.");

            var launcherConfig = Helpers.GetLauncherConfig(associatedLaunchers[0], appLocation);

            if (launcherConfig == null)
            {
                var initResponse = new AdminInit(appLocation).ShowDialog();
                this.Close();
                return;
            }

            this._config = launcherConfig;

            if (this._config == null || this._config.Servers == null || this._config.Servers.Count == 0)
            {
                MessageBox.Show(@"No servers configured, contact your server admin. Closing launcher.", @"No Servers Found", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                this.Load += (s, e) =>
                {
                    this.Close(); 
                    Application.Exit(); 
                };
            }

            InitializeComponent();

            // if the version info url is null, then don't display the "check"
            if(this._config.VersionInfoUrl == null)
            {
                this.btnCheck.Visible = false;
                this.btnSettings.Location = new Point(
                   this.btnClose.Location.X - this.btnSettings.Width - 5, 
                    this.btnSettings.Location.Y);
            }

            if(this._config.WebsiteUrl == null)
                this.pctLinLogo.Cursor = Cursors.Arrow;

            if (this._config.VoteUrl == null)
                this.pctVote.Visible = false;

            this.BannerBrowser.Url = this._config.NewsUrl;
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void btnSettings_Click(object sender, EventArgs e)
        {
            var settingsForm = new SettingsForm(this._config);
            settingsForm.ShowDialog();
            settingsForm.Dispose();
        }

        private void LauncherForm_Shown(object sender, EventArgs e)
        {
            var settings = Helpers.LoadSettings(this._config.ConfigType == ConfigType.Registry ? this._config.KeyName : this._config.InstallDir, 
                this._config.ConfigType);

            if (settings == null)
            {
                MessageBox.Show(@"There was an error loading your settings. If this was after an update, it may be intentional, however, " +
                @"if it happens often please file a bug report.", @"Error Loading Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                settings = new Settings();
            }
                
            this.lblVersion.Text = Version;
            this.cmbServer.Items.AddRange(this._config.Servers.Keys.ToArray());
            this.cmbServer.SelectedIndex = 0;

            this.configChecker.RunWorkerAsync();
        }

        private void playButton_Click(object sender, EventArgs e)
        {
            if(this._config.VersionInfoUrl != null)
            {
                var patchForm = new Patcher(this._config, this._hasUpdates);

                if (!patchForm.IsDisposed)
                {
                    patchForm.ShowDialog();
                }
            }

            this.Launch(this._config.Servers[this.cmbServer.SelectedItem.ToString()]);
        }

        private void Launch(Server server)
        {
            var settings = Helpers.LoadSettings(this._config.ConfigType == ConfigType.Registry ? this._config.KeyName : this._config.InstallDir, 
                this._config.ConfigType);
            var binFile = Path.GetFileNameWithoutExtension("TW13032701.bin");
            var binpath = Path.Combine(this._config.InstallDir, binFile);

            IPAddress[] ipOrDns;

            try
            {
                ipOrDns = Dns.GetHostAddresses(server.IpOrDns);
            }
            catch (SocketException)
            {
                MessageBox.Show(@"There was an error connecting to the server. Check the forums for any issues.",
                    @"Error Connecting!",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.CheckServerStatus(false);
                return;
            }

            if (!Lineage.Run(settings, binFile, this.cmbServer.SelectedItem.ToString()))
            {
                MessageBox.Show("There was an error applying your settings to the Lineage client. Try running it again!", "Error!",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void cmbServer_SelectedIndexChanged(object sender, EventArgs e)
        {
            this.lblServerStatus.Text = @"PINGING...";
            this.lblServerStatus.ForeColor = Color.Khaki;

            var statusThread = new Thread(() => this.CheckServerStatus(true)) { IsBackground = true };
            statusThread.Start();
        }

        private bool CheckServerStatus(bool threaded)  
        {
            var returnValue = false;

            var host = string.Empty;
            var port = -1;

            //add a delay so the "pinging..." actually shows up. Otherwise it kind of looks
            //like it isn't trying to check
            if (threaded)
            {
                Thread.Sleep(500);
                cmbServer.Invoke(new Action(
                    () =>
                    {
                        host = this._config.Servers[this.cmbServer.Text].IpOrDns;
                        port = this._config.Servers[this.cmbServer.Text].Port;
                    }));
            }
            else
            {
                host = this._config.Servers[this.cmbServer.Text].IpOrDns;
                port = this._config.Servers[this.cmbServer.Text].Port;
            }

            //should never happen, but let's handle it just in case
            if (host == string.Empty || port == -1)
                return false;

            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    var result = socket.BeginConnect(host, port, null, null);
                    result.AsyncWaitHandle.WaitOne(2000, true);

                    if (!socket.Connected)
                    {
                        Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "Text", "OFFLINE");
                        Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "ForeColor", Color.Red);
                    }
                    else
                    {
                        returnValue = true;
                        Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "Text", "ONLINE");
                        Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "ForeColor", Color.Green);

                        var updateCheckComplete = false;

                        if (updateCheckComplete)
                            Helpers.SetControlPropertyThreadSafe(this.btnPlay, "Enabled", true);
                    }
                }
                catch
                {
                    Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "Text", "OFFLINE");
                    Helpers.SetControlPropertyThreadSafe(this.lblServerStatus, "ForeColor", Color.Red);
                }
                finally
                {
                    socket.Close();
                } //end try/catch/finally
            } //end using

            return returnValue;
        } //end checkServerStatus

        private void pctVote_Click(object sender, EventArgs e)
        {
            if (this._config.VoteUrl == null)
                return;

            var voteUrl = new ProcessStartInfo(this._config.VoteUrl.ToString());
            Process.Start(voteUrl);
        } //end pctVote_Click

        private void pctLinLogo_Click(object sender, EventArgs e)
        {
            if (this._config.WebsiteUrl == null)
                return;

            var voteUrl = new ProcessStartInfo(this._config.WebsiteUrl.ToString());
            Process.Start(voteUrl);
        }//end pctLinLogo_Click

        private void LauncherForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || !e.Shift || e.KeyCode != Keys.A)
                return;

            var addServerForm = new AddServerForm();

            if (addServerForm.ShowDialog() == DialogResult.OK)
            {
                if (this._config.Servers.ContainsKey(addServerForm.txtName.Text))
                {
                    MessageBox.Show("A server with that name has already been added.");
                    return;
                }

                this._config.Servers.Add(addServerForm.txtName.Text, new Server
                {
                    IpOrDns = addServerForm.txtIpAddress.Text,
                    Port = Convert.ToInt32(addServerForm.txtPort.Text)
                });

                if(addServerForm.chkPermanent.Checked)
                {
                    Helpers.SetConfigValue(this._config.KeyName,
                        "Servers",
                        string.Format(",{0}:{1}:{2}", addServerForm.txtName.Text, addServerForm.txtIpAddress.Text, addServerForm.txtPort.Text)
                        , true);
                }
                
                this.cmbServer.Items.Add(addServerForm.txtName.Text);
                this.cmbServer.SelectedIndex = cmbServer.Items.Count - 1;
            } //end if
        } //end LauncherForm_KeyDown

        private void updateChecker_DoWork(object sender, DoWorkEventArgs e)
        {
            var versionInfo = Helpers.GetVersionInfo(this._config.VersionInfoUrl, this._config.PublicKey);
            var launcherKey = Registry.CurrentUser.OpenSubKey(@"Software\" + this._config.KeyName, true);
            var lastUpdatedCheck = launcherKey.GetValue("LastUpdated");
            var updatesLastRun = (int?)lastUpdatedCheck ?? 0;

            if (versionInfo == null)
                return;

            this._hasUpdates = versionInfo.Files.Any(b => b.Value > updatesLastRun);
        } //end updateChecker

        private void updateChecker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.btnPlay.Enabled = true;
        } //end updateChecker_RunWorkerCompleted

        private void btnCheck_Click(object sender, EventArgs e)
        {
            this.btnCheck.Enabled = false;

            var patchForm = new Patcher(this._config, true, true);

            if (!patchForm.IsDisposed)
            {
                patchForm.ShowDialog();
            }

            this.btnCheck.Enabled = true;
        } //end btnCheck_Click

        private void systemIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            systemIcon.Visible = false;
            this.Show();
        }

        private void Restore_Click(object sender, EventArgs e)
        {
            systemIcon.Visible = false;
            this.Show();
        }

        private void Close_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void configChecker_DoWork(object sender, DoWorkEventArgs e)
        {
            this._versionInfo = Helpers.GetVersionInfo(this._config.VersionInfoUrl, this._config.PublicKey);

            var force = e.Argument != null && (bool)e.Argument;
            var launcherKey = Registry.CurrentUser.OpenSubKey(@"Software\" + this._config.KeyName, true);
            var lastUpdatedCheck = launcherKey.GetValue("LastUpdated");
            var updatesLastRun = (int?)lastUpdatedCheck ?? 0;

            if (this._versionInfo == null)
                return;

            var settings = Helpers.LoadSettings(
                this._config.ConfigType == ConfigType.Registry ? this._config.KeyName : this._config.InstallDir, 
                this._config.ConfigType);

            if (Helpers.UpdateConfig(this._versionInfo))
            {
                MessageBox.Show("Configuration information was updated from the server.\n\nThe launcher will close. Please re-launch.",
                    @"Configuration Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);

                this.Invoke((MethodInvoker)delegate
                {
                    this.Close();
                });
            }

            if (Helpers.StringToNumber(this._versionInfo.Version) > Helpers.StringToNumber(Version))
            {
                var applicationPath = Application.ExecutablePath;
                var appDataPath = Directory.GetParent(Application.UserAppDataPath).ToString();
                var updaterLocation = Path.Combine(appDataPath, "Updater.exe");
                var updaterChecksum = Helpers.GetChecksum(updaterLocation);

                var result = DialogResult.Cancel;

                // push to the UI thread to actually display the dialog... ugly hack
                var dialog = this.BeginInvoke(new MethodInvoker(delegate
                {
                    result = new UpdateForm(this._versionInfo).ShowDialog();
                }));

                this.EndInvoke(dialog);

                if (result == DialogResult.OK)
                {
                    var info = new ProcessStartInfo(updaterLocation);
                    info.Arguments = "\"" + applicationPath + "\"";

                    if (Environment.OSVersion.Version.Major >= 6)
                        info.Verb = "runas";

                    Process.Start(info);
                } //end if
            } //end if

            Helpers.SetControlPropertyThreadSafe(this.btnPlay, "Enabled", true);
        }

        private void BannerBrowser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            var browser = sender as WebBrowser;

            if(this._config.NewsUrl != null && browser != null && browser.Document.Url.ToString() == this._config.NewsUrl.ToString())
            {
                this.BannerBrowser.Visible = true;
            }
        }

        private void BannerBrowser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            if(this._config.NewsUrl != null && e.Url.ToString() != this._config.NewsUrl.ToString())
            {
                //cancel the current event
                e.Cancel = true;

                //this opens the URL in the user's default browser
                Process.Start(e.Url.ToString());
            }
        }

        private void configChecker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.updateChecker.RunWorkerAsync();
        }
    } //end class
} //end namespace
