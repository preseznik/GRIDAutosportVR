Imports System.Text.Json
Imports System.Windows.Forms

Public Module Program
    <STAThread>
    Public Function Main(args As String()) As Integer
        Dim inspect = args.Length > 0 AndAlso args(0) = "--inspect"
        Dim play = args.Length > 0 AndAlso args(0) = "--play-vr"
        Dim restore = args.Length > 0 AndAlso args(0) = "--restore-play"
        Dim options = If(inspect OrElse play OrElse restore, args.Skip(1).ToArray(), args)
        If ((play OrElse restore) AndAlso options.Length <> 2) OrElse (options.Length <> 0 AndAlso (options.Length <> 2 OrElse options(0) <> "--game-dir")) Then
            Console.Error.WriteLine("Usage: GRIDAutosportVR.Launcher.exe [--inspect] [--game-dir FOLDER] or (--play-vr | --restore-play) --game-dir FOLDER")
            Return 64
        End If
        Try
            Dim folder = If(options.Length = 2, options.Last(), Installation.FindGame())
            If restore Then
                Dim recovered = VrPlaySession.Recover(folder)
                Console.WriteLine(If(recovered, "Restored the stock game files.", "No ordinary VR play session to restore."))
                Return 0
            End If
            Dim probe = Installation.FindProbe(AppContext.BaseDirectory)
            Dim renderer = Installation.FindRenderer(AppContext.BaseDirectory)
            If play Then
                If Not Installation.GameExists(folder) Then Throw New IOException("Select the Steam installation of GRID Autosport.")
                VrPlaySession.Recover(folder)
                Dim problem = Installation.VrLaunchProblem(folder, probe, renderer)
                If problem IsNot Nothing Then Throw New IOException(problem)
                Directory.CreateDirectory(Installation.LogDirectory)
                Dim logFile = Path.Combine(Installation.LogDirectory, $"launch-openxr-{Date.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log")
                Using check = Process.Start(Installation.ProbeStartInfo(probe, False, logFile))
                    check.WaitForExit()
                    Dim failure = Installation.ProbeError(check.ExitCode)
                    If failure IsNot Nothing Then Throw New IOException(failure & Environment.NewLine & logFile)
                End Using
                Return VrPlaySession.Run(folder, renderer)
            End If
            If inspect Then
                Dim valid = Installation.GameExists(folder)
                Dim launch = If(valid, Installation.DesktopStartInfo(folder), Nothing)
                Dim vr = If(valid, Installation.VrStartInfo(folder), Nothing)
                Dim vrProblem = Installation.VrLaunchProblem(folder, probe, renderer)
                Dim sessionArgs As String() = Nothing
                If probe IsNot Nothing Then sessionArgs = Installation.ProbeStartInfo(probe, True, "session.log").ArgumentList.ToArray()
                Console.WriteLine(JsonSerializer.Serialize(New With {
                    .gameFolder = folder, .gameDetected = valid, .desktopTarget = launch?.FileName, .desktopArguments = launch?.ArgumentList.ToArray(),
                    .probePath = probe, .eyeTestArguments = sessionArgs, .rendererPath = renderer, .vrLaunchAvailable = vrProblem Is Nothing,
                    .vrLaunchProblem = vrProblem, .vrTarget = vr?.FileName, .vrArguments = vr?.ArgumentList.ToArray(), .runtimeCheck = "on launch",
                    .settingsFile = LauncherSettings.SettingsPath, .hudFollowView = LauncherSettings.ReadHudFollowView(LauncherSettings.SettingsPath)}))
                Return If(valid, 0, 2)
            End If
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
            Application.SetColorMode(SystemColorMode.System)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)
            Using window As New MainForm(folder, probe)
                Application.Run(window)
            End Using
            Return 0
        Catch ex As Exception
            If inspect OrElse play OrElse restore Then
                Console.Error.WriteLine(ex.Message)
            Else
                MessageBox.Show(ex.Message, "GRID Autosport VR", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End If
            Return 1
        End Try
    End Function
End Module
