Imports System.Drawing
Imports System.Windows.Forms

' Adapted from the DiRT3VR MainForm. Keep its tab strip, selectable status, DPI
' sizing and launch footer while exposing only actions Autosport supports.
Friend Class MainForm
    Inherits Form

    Private gameFolder As String
    Private ReadOnly probePath As String
    Private ReadOnly rendererPath As String = Installation.FindRenderer(AppContext.BaseDirectory)
    Private ReadOnly logDirectory As String = Installation.LogDirectory
    Private ReadOnly gamePath As New TextBox With {.ReadOnly = True, .Dock = DockStyle.Fill, .AccessibleName = "Steam game folder"}
    Private ReadOnly status As New SelectableStatusText With {.AccessibleName = "Launcher status", .Margin = New Padding(0, 12, 0, 10)}
    Private ReadOnly desktop As New Button With {.Text = "Launch desktop", .AutoSize = True}
    Private ReadOnly vr As New Button With {.Text = "Launch VR", .AutoSize = True}
    Private ReadOnly browse As New Button With {.Text = "Browse…", .AutoSize = True}
    Private ReadOnly query As New Button With {.Text = "Check runtime", .AutoSize = True}
    Private ReadOnly pattern As New Button With {.Text = "Show eye markers", .AutoSize = True}
    Private ReadOnly logs As New Button With {.Text = "Open logs", .AutoSize = True}
    Private Const HudHelp As String = "Off keeps the race HUD at your seated center. On moves it with your head. Loading screens stay in place in both modes."
    Private ReadOnly hudFollow As New CheckBox With {.Name = "HudFollowView", .Text = "HUD follows view", .AccessibleDescription = HudHelp, .AutoSize = True, .Margin = New Padding(3, 12, 3, 0)}
    Private ReadOnly brandLogo As New PictureBox With {.SizeMode = PictureBoxSizeMode.Zoom, .Margin = New Padding(4), .AccessibleName = "GRID Autosport VR logo"}
    Private ReadOnly brandIcon As Icon
    Private testing As Boolean
    Private supervising As Boolean
    Private recoveryError As String

    Public Sub New(folder As String, probe As String)
        gameFolder = folder
        probePath = probe
        Text = "GRID Autosport VR"
        Dim assembly = GetType(MainForm).Assembly
        Using stream = assembly.GetManifestResourceStream("GRIDAutosportVR.logo.ico"), original As New Icon(stream)
            brandIcon = DirectCast(original.Clone(), Icon)
            Icon = brandIcon
        End Using
        Using stream = assembly.GetManifestResourceStream("GRIDAutosportVR.logo-display.png"), original As New Bitmap(stream)
            brandLogo.Image = New Bitmap(original)
        End Using
        Font = New Font("Segoe UI", 10)
        AutoScaleMode = AutoScaleMode.Dpi
        brandLogo.Size = LogicalToDeviceUnits(New Size(88, 88))
        MinimumSize = LogicalToDeviceUnits(New Size(560, 470))
        ClientSize = LogicalToDeviceUnits(New Size(760, 500))
        StartPosition = FormStartPosition.CenterScreen

        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(20), .ColumnCount = 1, .RowCount = 4}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For Each row In {New RowStyle(SizeType.AutoSize), New RowStyle(SizeType.Percent, 100), New RowStyle(SizeType.AutoSize), New RowStyle(SizeType.AutoSize)}
            layout.RowStyles.Add(row)
        Next
        Dim header As New TableLayoutPanel With {.Name = "BrandHeader", .ColumnCount = 2, .AutoSize = True, .Dock = DockStyle.Fill, .Margin = New Padding(0, 0, 0, 12)}
        header.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        header.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        header.Controls.Add(brandLogo, 0, 0)
        header.Controls.Add(New Label With {.Text = Text, .AutoSize = True, .Anchor = AnchorStyles.Left, .Font = New Font(Font.FontFamily, 20, FontStyle.Bold), .Margin = New Padding(10, 0, 4, 0)}, 1, 0)
        layout.Controls.Add(header, 0, 0)
        Dim tabs As New ModernTabs With {.Dock = DockStyle.Fill, .Name = "LauncherTabs"}
        layout.Controls.Add(tabs, 0, 1)
        layout.Controls.Add(status, 0, 2)
        AddHandler layout.SizeChanged, Sub() status.FitWidth(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal))

        Dim game = AddPage(tabs, "Launcher")
        game.Controls.Add(New Label With {.Text = "Steam game folder", .AutoSize = True})
        Dim folderRow As New TableLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True, .ColumnCount = 2}
        folderRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        folderRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        folderRow.Controls.Add(gamePath, 0, 0)
        folderRow.Controls.Add(browse, 1, 0)
        game.Controls.Add(folderRow)
        game.Controls.Add(New Label With {.Text = "Connect your headset before launching in VR.", .AutoSize = True, .Margin = New Padding(3, 12, 3, 3)})
        game.Controls.Add(hudFollow)
        game.Controls.Add(New Label With {.Text = HudHelp, .AutoSize = True, .Margin = New Padding(3, 3, 3, 0)})
        AddHandler browse.Click, AddressOf ChooseGame

        Dim runtime = AddPage(tabs, "OpenXR")
        runtime.Controls.Add(New Label With {.Text = "Connect your headset to check the 32-bit OpenXR runtime. The eye test displays a marker in each eye for up to 20 seconds.", .AutoSize = True})
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True}
        actions.Controls.AddRange({query, pattern})
        runtime.Controls.Add(actions)
        If probePath Is Nothing Then runtime.Controls.Add(New Label With {.Text = "Place AutosportXRProbe.exe in this package’s probe folder to enable these tests.", .AutoSize = True})

        Dim footer As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill}
        footer.Controls.AddRange({desktop, vr, logs})
        layout.Controls.Add(footer, 0, 3)
        Controls.Add(layout)
        AddHandler desktop.Click, Sub() RunDesktop()
        AddHandler vr.Click, Async Sub() Await RunVrAsync()
        AddHandler query.Click, Async Sub() Await RunProbeAsync(False)
        AddHandler pattern.Click, Async Sub() Await RunProbeAsync(True)
        AddHandler logs.Click, Sub()
                                   Try
                                       Process.Start(New ProcessStartInfo(logDirectory) With {.UseShellExecute = True})?.Dispose()
                                   Catch ex As Exception
                                       status.Text = ex.Message
                                   End Try
                               End Sub
        LauncherAppearance.Apply(Me)
        RefreshInstallation()
        Try
            hudFollow.Checked = LauncherSettings.ReadHudFollowView(LauncherSettings.SettingsPath)
            AddHandler hudFollow.CheckedChanged, AddressOf SaveHudPreference
        Catch ex As Exception
            hudFollow.Enabled = False
            status.Text = "Could not read HUD settings. " & ex.Message
        End Try
    End Sub

    Private Sub SaveHudPreference(sender As Object, e As EventArgs)
        Try
            LauncherSettings.SaveHudFollowView(LauncherSettings.SettingsPath, hudFollow.Checked)
            status.Text = "HUD setting saved for the next VR race."
        Catch ex As Exception
            RemoveHandler hudFollow.CheckedChanged, AddressOf SaveHudPreference
            hudFollow.Checked = Not hudFollow.Checked
            AddHandler hudFollow.CheckedChanged, AddressOf SaveHudPreference
            status.Text = "Could not save the HUD setting. " & ex.Message
        End Try
    End Sub

    Private Shared Function AddPage(tabs As TabControl, title As String) As TableLayoutPanel
        Dim page As New TabPage(title) With {.Padding = New Padding(16), .AutoScroll = True, .UseVisualStyleBackColor = False}
        Dim rows As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 1, .GrowStyle = TableLayoutPanelGrowStyle.AddRows}
        rows.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        page.Controls.Add(rows)
        tabs.TabPages.Add(page)
        Return rows
    End Function

    Private Sub UpdateActions()
        gamePath.Text = If(gameFolder, "")
        Dim idle = Not testing AndAlso Not supervising
        Dim valid = Installation.GameExists(gameFolder)
        desktop.Enabled = idle AndAlso valid AndAlso recoveryError Is Nothing AndAlso
            Not File.Exists(Path.Combine(gameFolder, "gasvr-play-session.json")) AndAlso
            Not File.Exists(Path.Combine(gameFolder, "gasvr-benchmark-session.json"))
        Dim problem = If(recoveryError, Installation.VrLaunchProblem(gameFolder, probePath, rendererPath))
        vr.Enabled = idle AndAlso problem Is Nothing
        vr.AccessibleDescription = If(problem, "Start GRID Autosport with the 32-bit OpenXR bridge.")
        browse.Enabled = idle
        query.Enabled = idle AndAlso File.Exists(probePath)
        pattern.Enabled = query.Enabled
        logs.Enabled = Directory.Exists(logDirectory)
    End Sub

    Private Sub RefreshInstallation()
        recoveryError = Nothing
        Dim recovered As Boolean
        If Installation.GameExists(gameFolder) Then
            Try
                recovered = VrPlaySession.Recover(gameFolder)
            Catch ex As Exception
                recoveryError = ex.Message
            End Try
        End If
        UpdateActions()
        status.Text = If(recovered, "Restored the game files from the interrupted VR session.",
                         If(vr.Enabled, "Steam installation detected.", vr.AccessibleDescription))
    End Sub

    Private Sub ChooseGame(sender As Object, e As EventArgs)
        Using dialog As New FolderBrowserDialog With {.Description = "Select the GRID Autosport folder in your Steam library.", .ShowNewFolderButton = False, .UseDescriptionForTitle = True}
            If gameFolder IsNot Nothing Then dialog.InitialDirectory = gameFolder
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            If Not Installation.GameExists(dialog.SelectedPath) Then
                status.Text = "This folder does not match the GRID Autosport Steam manifest."
                Return
            End If
            gameFolder = dialog.SelectedPath
            RefreshInstallation()
        End Using
    End Sub

    Private Async Function RunVrAsync() As Task
        If supervising OrElse testing Then Return
        supervising = True
        UpdateActions()
        Try
            status.Text = "VR session in progress. Steam opens the game after the headset check."
            Using worker = Process.Start(Installation.VrStartInfo(gameFolder))
                Dim errors = worker.StandardError.ReadToEndAsync()
                Await worker.WaitForExitAsync()
                Dim detail = (Await errors).Trim()
                If Not IsDisposed Then
                    status.Text = If(worker.ExitCode = 0, "VR session ended. Stock game files restored.",
                                     If(detail.Length > 0, detail, "VR session exited with code " & worker.ExitCode.ToString() & "."))
                End If
            End Using
        Catch ex As Exception
            If Not IsDisposed Then status.Text = ex.Message
        Finally
            supervising = False
            If Not IsDisposed Then UpdateActions()
        End Try
    End Function

    Private Sub RunDesktop()
        Try
            Process.Start(Installation.DesktopStartInfo(gameFolder))?.Dispose()
            status.Text = "Launch request sent to Steam."
        Catch ex As Exception
            status.Text = ex.Message
        End Try
        UpdateActions()
    End Sub

    Private Async Function RunProbeAsync(stereo As Boolean) As Task
        If testing Then Return
        testing = True
        UpdateActions()
        Try
            Directory.CreateDirectory(logDirectory)
            Dim filename = If(stereo, "eye-test-", "openxr-") & Date.Now.ToString("yyyyMMdd-HHmmss-fff") & ".log"
            Dim logfile = Path.Combine(logDirectory, filename)
            status.Text = If(stereo, "Running the headset eye test…", "Checking the Win32 OpenXR runtime…")
            Using worker = Process.Start(Installation.ProbeStartInfo(probePath, stereo, logfile))
                Await worker.WaitForExitAsync()
                status.Text = If(Installation.ProbeError(worker.ExitCode), If(stereo, "The probe submitted its test pattern.", "Runtime check passed."))
            End Using
            status.Text &= If(File.Exists(logfile), Environment.NewLine & logfile, " The probe did not create its log.")
        Catch ex As Exception
            status.Text = ex.Message
        Finally
            testing = False
            UpdateActions()
        End Try
    End Function

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If testing AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            status.Text = "Waiting for the OpenXR probe to finish before closing."
        End If
        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            brandLogo.Image?.Dispose()
            brandLogo.Image = Nothing
            Icon = Nothing
            brandIcon?.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
