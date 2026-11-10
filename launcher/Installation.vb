Imports System.Text.RegularExpressions

Friend Module Installation
    Public Const AppId As String = "255220"
    Public Const GameName As String = "GRID Autosport"
    Private Const ProbeName As String = "AutosportXRProbe.exe"
    Private Const LauncherName As String = "GRIDAutosportVR.Launcher.exe"

    Public ReadOnly Property LogDirectory As String
        Get
            Return Path.Combine(Path.GetDirectoryName(LauncherSettings.SettingsPath), "logs")
        End Get
    End Property

    ' Steam opens the registered application. Match the selected installation to its
    ' library manifest before enabling launch, including when the user browses to it.
    Public Function GameExists(folder As String) As Boolean
        If String.IsNullOrWhiteSpace(folder) OrElse Not Path.IsPathFullyQualified(folder) Then Return False
        Try
            Dim game As New DirectoryInfo(folder)
            Dim common = game.Parent
            If common Is Nothing OrElse Not String.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase) OrElse common.Parent Is Nothing Then Return False
            If Not File.Exists(Path.Combine(game.FullName, "GRIDAutosport.exe")) Then Return False
            Dim manifest = Path.Combine(common.Parent.FullName, "appmanifest_" & AppId & ".acf")
            If Not File.Exists(manifest) Then Return False
            Dim fields = File.ReadAllText(manifest)
            Return Regex.IsMatch(fields, "(?m)^\s*""appid""\s+""" & AppId & """\s*$") AndAlso
                Regex.IsMatch(fields, "(?im)^\s*""installdir""\s+""" & Regex.Escape(game.Name) & """\s*$")
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            Return False
        End Try
    End Function

    Public Function FindGame() As String
        Dim own As New DirectoryInfo(AppContext.BaseDirectory)
        For Each folder In {own.FullName, own.Parent?.FullName}
            If GameExists(folder) Then Return folder
        Next
        Dim conventional = DriveInfo.GetDrives().Where(Function(d) d.DriveType = DriveType.Fixed).
            Select(Function(d) Path.Combine(d.Name, "SteamLibrary"))
        For Each library In SteamLibrary.SteamLibraries().Concat(conventional).Distinct(StringComparer.OrdinalIgnoreCase)
            Dim folder = Path.Combine(library, "steamapps", "common", GameName)
            If GameExists(folder) Then Return folder
        Next
        Return Nothing
    End Function

    Public Function DesktopStartInfo(folder As String) As ProcessStartInfo
        If Not GameExists(folder) Then Throw New IOException("Select the Steam installation of GRID Autosport.")
        Dim client = SteamLibrary.SteamClient()
        If client IsNot Nothing Then
            ' The URI can stop at the standard/touch chooser. Ask Steam for its
            ' default desktop entry explicitly, as the stock benchmark does.
            Dim start As New ProcessStartInfo(client) With {.UseShellExecute = False, .WorkingDirectory = Path.GetDirectoryName(client)}
            start.ArgumentList.Add("-applaunch")
            start.ArgumentList.Add(AppId)
            Return start
        End If
        Return New ProcessStartInfo("steam://rungameid/" & AppId) With {.UseShellExecute = True}
    End Function

    Public Function FindProbe(baseDirectory As String) As String
        Dim package As New DirectoryInfo(baseDirectory)
        Dim candidates = {Path.Combine(package.FullName, "probe", ProbeName), Path.Combine(package.FullName, ProbeName)}
        Return candidates.FirstOrDefault(AddressOf File.Exists)
    End Function

    Public Function FindRenderer(baseDirectory As String) As String
        Dim dll = Path.Combine(baseDirectory, "renderer", "gasxr.dll")
        Return If(File.Exists(dll), dll, Nothing)
    End Function

    Public Function VrLaunchProblem(folder As String, probe As String, renderer As String) As String
        If Not GameExists(folder) Then Return "Choose your GRID Autosport Steam folder."
        If Not File.Exists(renderer) Then Return "The launcher package is missing renderer\gasxr.dll."
        If Not File.Exists(probe) Then Return "The launcher package is missing the 32-bit OpenXR probe."
        If SteamLibrary.SteamClient() Is Nothing Then Return "The Steam client could not be located."
        If File.Exists(Path.Combine(folder, "gasvr-benchmark-session.json")) Then Return "Finish or restore the VR benchmark before launching the game."
        If File.Exists(Path.Combine(folder, "gasvr-play-session.json")) Then Return "A VR session is active or needs recovery. After the game exits, reopen the launcher."
        If File.Exists(Path.Combine(folder, "gasxr.dll")) Then Return "A gasxr.dll is already installed in the game folder. Check that file before launching VR."
        Dim executable = Path.Combine(folder, "GRIDAutosport_avx.exe")
        If Not File.Exists(executable) Then Return "The Steam installation is missing GRIDAutosport_avx.exe."
        Try
            VrPlaySession.EnsureIdle(folder)
            Using stream = File.OpenRead(executable)
                Dim digest = Convert.ToHexString(Security.Cryptography.SHA256.HashData(stream))
                If Not digest.Equals(VrPlaySession.StockSha256, StringComparison.OrdinalIgnoreCase) Then Return "This version of GRID Autosport is unsupported. Verify the game files in Steam."
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ex.Message
        End Try
        Return Nothing
    End Function

    Public Function VrStartInfo(folder As String) As ProcessStartInfo
        Dim start As New ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, LauncherName)) With {
            .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardError = True, .WorkingDirectory = AppContext.BaseDirectory}
        start.ArgumentList.Add("--play-vr")
        start.ArgumentList.Add("--game-dir")
        start.ArgumentList.Add(folder)
        Return start
    End Function

    Public Function ProbeError(exitCode As Integer) As String
        Select Case exitCode
            Case 0 : Return Nothing
            Case 2 : Return "Connect your headset to the PC, then try again. The 32-bit OpenXR runtime could not find a headset."
            Case 124 : Return "The 32-bit OpenXR runtime timed out."
            Case Else : Return "The 32-bit OpenXR probe failed with exit code " & exitCode.ToString() & "."
        End Select
    End Function

    Public Function ProbeStartInfo(probe As String, stereo As Boolean, logFile As String) As ProcessStartInfo
        If String.IsNullOrEmpty(probe) OrElse Not File.Exists(probe) Then Throw New FileNotFoundException("The Win32 OpenXR probe is missing.")
        Dim start As New ProcessStartInfo(probe) With {
            .UseShellExecute = False, .CreateNoWindow = True, .WorkingDirectory = Path.GetDirectoryName(probe)}
        If stereo Then
            start.ArgumentList.Add("--session")
            start.ArgumentList.Add("--seconds")
            start.ArgumentList.Add("20")
        Else
            start.ArgumentList.Add("--query")
        End If
        start.ArgumentList.Add("--log")
        start.ArgumentList.Add(logFile)
        Return start
    End Function
End Module
