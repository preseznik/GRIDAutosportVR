Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Microsoft.Win32.SafeHandles

Friend Module VrPlaySession
    Friend Const StockSha256 As String = "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48"
    Private Const VrSha256 As String = "9d3e8f9a36571e20707187f1da86967201ab74e72f9f72e8ec446f01dbe59856"
    Private Const ImportNameOffset As Integer = &HE0C23C
    Private Const Avx As String = "GRIDAutosport_avx.exe"
    Private Const BridgeName As String = "gasxr.dll"
    Private Const LeaseName As String = "gasvr-play-session.json"
    Private Const BenchmarkLease As String = "gasvr-benchmark-session.json"
    Private Const SessionMutex As String = "Local\GRIDAutosportVR-255220"

    Private Class Lease
        Public Property Id As String
        Public Property GameFolder As String
        Public Property BridgeHash As String
    End Class

    Private Function SessionFolder(id As String) As String
        Return Path.Combine(Path.GetDirectoryName(LauncherSettings.SettingsPath), "sessions", id)
    End Function

    Private Function Hash(bytes As Byte()) As String
        Return Convert.ToHexStringLower(SHA256.HashData(bytes))
    End Function

    Private Function FileHash(fileName As String) As String
        Using input = File.OpenRead(fileName)
            Return Convert.ToHexStringLower(SHA256.HashData(input))
        End Using
    End Function

    Private Sub WriteNew(fileName As String, bytes As Byte())
        Using output As New FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            output.Write(bytes)
            output.Flush(True)
        End Using
    End Sub

    Private Sub Record(id As String, message As String)
        File.AppendAllText(Path.Combine(SessionFolder(id), "supervisor.log"),
                           Date.UtcNow.ToString("O") & " " & message & Environment.NewLine)
    End Sub

    Private Function UnderLock(Of T)(action As Func(Of T)) As T
        Using gate As New Mutex(False, SessionMutex)
            Try
                If Not gate.WaitOne(0) Then Throw New IOException("A GRID Autosport VR session is already running.")
            Catch ex As AbandonedMutexException
                ' The former supervisor exited. Its durable lease still describes the installation.
            End Try
            Try
                Return action()
            Finally
                gate.ReleaseMutex()
            End Try
        End Using
    End Function

    Friend Sub EnsureIdle(Optional folder As String = Nothing)
        For Each processName As String In {"GRIDAutosport", "GRIDAutosport_avx"}
            For Each entry In Process.GetProcessesByName(processName)
                Using entry
                    Try
                        If entry.HasExited Then Continue For
                        Dim imageName = entry.MainModule?.FileName
                        If folder IsNot Nothing AndAlso Not String.IsNullOrEmpty(imageName) AndAlso
                           Not String.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Catch ex As InvalidOperationException When entry.HasExited
                        Continue For
                    Catch ex As Win32Exception
                        Throw New IOException("Cannot verify the running game's installation. Close GRID Autosport before continuing.", ex)
                    End Try
                    Throw New IOException("Close GRID Autosport before changing its VR installation.")
                End Using
            Next
        Next
    End Sub

    Private Function CheckedFolder(folder As String) As String
        If Not Installation.GameExists(folder) Then Throw New IOException("Select the registered Steam installation of GRID Autosport.")
        Return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
    End Function

    Public Function Run(gameFolder As String, bridgePath As String) As Integer
        Dim folder = CheckedFolder(gameFolder)
        Dim client = SteamLibrary.SteamClient()
        If client Is Nothing Then Throw New IOException("Steam.exe was not found. Start Steam, then try again.")
        Dim library = Directory.GetParent(folder).Parent.Parent.FullName
        If Not SteamLibrary.SteamLibraries().Any(Function(root) String.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), library,
                                                                               StringComparison.OrdinalIgnoreCase)) Then
            Throw New IOException("The selected installation is outside Steam's registered libraries.")
        End If
        Dim hudFollow = LauncherSettings.ReadHudFollowView(LauncherSettings.SettingsPath)
        Return UnderLock(Function()
                             If File.Exists(Path.Combine(folder, BenchmarkLease)) Then Throw New IOException("Restore the outstanding benchmark session first.")
                             EnsureIdle(folder)
                             RestoreLease(folder)
                             Dim id = Guid.NewGuid().ToString("N")
                             Try
                                 Stage(folder, bridgePath, id)
                                 Dim start As New ProcessStartInfo(client) With {.UseShellExecute = False, .WorkingDirectory = Path.GetDirectoryName(client)}
                                 For Each arg In {"-applaunch", Installation.AppId, "-gasvrplay=" & id, "-gasvrmode=openxr",
                                                  "-gasvrhudfollow=" & If(hudFollow, "1", "0")}
                                     start.ArgumentList.Add(arg)
                                 Next
                                 Record(id, "Requesting normal play through Steam.")
                                 Dim requested = Date.UtcNow
                                 Using steam = Process.Start(start)
                                     If steam Is Nothing Then Throw New IOException("Steam could not start GRID Autosport.")
                                 End Using
                                 Return WaitForGame(folder, id, requested)
                             Finally
                                 If File.Exists(Path.Combine(folder, LeaseName)) Then RestoreLease(folder)
                             End Try
                         End Function)
    End Function

    Public Function Recover(gameFolder As String) As Boolean
        Dim folder = CheckedFolder(gameFolder)
        If Not File.Exists(Path.Combine(folder, LeaseName)) Then Return False
        Return UnderLock(Function() RestoreLease(folder))
    End Function

    ' The marker and stock backup are committed before the lease. The lease is
    ' committed before either game file is changed, including on an interrupted launch.
    Private Sub Stage(folder As String, bridgePath As String, id As String)
        Dim executable = Path.Combine(folder, Avx)
        Dim dll = Path.Combine(folder, BridgeName)
        If File.Exists(dll) OrElse File.Exists(Path.Combine(folder, LeaseName)) Then
            Throw New IOException("A VR bridge or play session already exists in the game folder. Existing files were preserved.")
        End If
        Dim stock = File.ReadAllBytes(executable)
        If Hash(stock) <> StockSha256 OrElse Not stock.AsSpan(ImportNameOffset, 10).SequenceEqual(Encoding.ASCII.GetBytes("d3d11.dll" & ChrW(0))) Then
            Throw New IOException("This GRID Autosport AVX executable is not the supported Steam build.")
        End If
        Dim candidate = DirectCast(stock.Clone(), Byte())
        Encoding.ASCII.GetBytes(BridgeName).CopyTo(candidate, ImportNameOffset)
        If Hash(candidate) <> VrSha256 Then Throw New IOException("The VR executable verification failed.")
        Dim payload = File.ReadAllBytes(bridgePath)
        Dim info As New Lease With {.Id = id, .GameFolder = folder, .BridgeHash = Hash(payload)}
        Dim saved = SessionFolder(id)
        Directory.CreateDirectory(saved)
        WriteNew(Path.Combine(saved, "session.id"), Encoding.ASCII.GetBytes(id))
        WriteNew(Path.Combine(saved, "original-avx.exe"), stock)
        If FileHash(Path.Combine(saved, "original-avx.exe")) <> StockSha256 Then Throw New IOException("The stock backup verification failed.")
        Dim prefix = Path.Combine(folder, "gasvr-play-" & id)
        WriteNew(prefix & ".lease.tmp", JsonSerializer.SerializeToUtf8Bytes(info))
        File.Move(prefix & ".lease.tmp", Path.Combine(folder, LeaseName))
        WriteNew(prefix & ".dll.tmp", payload)
        If FileHash(prefix & ".dll.tmp") <> info.BridgeHash Then Throw New IOException("The staged bridge verification failed.")
        File.Move(prefix & ".dll.tmp", dll)
        WriteNew(prefix & ".exe.tmp", candidate)
        EnsureIdle(folder)
        If FileHash(executable) <> StockSha256 OrElse FileHash(prefix & ".exe.tmp") <> VrSha256 Then Throw New IOException("The game files changed during staging.")
        File.Replace(prefix & ".exe.tmp", executable, Nothing)
        If FileHash(executable) <> VrSha256 Then Throw New IOException("The installed VR executable verification failed.")
        Record(id, "Staged verified AVX executable and bridge " & info.BridgeHash & ".")
    End Sub

    Private Sub RemoveMatching(fileName As String, expected As String, id As String)
        Dim timer = Stopwatch.StartNew()
        Dim retried As Boolean
        Do
            Try
                ' Hash the actual contents for each attempt. Do not allow writers
                ' between verification and delete; allow our open handle to be deleted.
                Using input As New FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read Or FileShare.Delete)
                    If Convert.ToHexStringLower(SHA256.HashData(input)) <> expected Then Throw New IOException("Preserving independently changed file: " & fileName)
                    File.Delete(fileName)
                End Using
                If retried Then Record(id, $"Released staged bridge after {timer.ElapsedMilliseconds} ms.")
                Return
            Catch ex As FileNotFoundException
                Return
            Catch ex As Exception When (TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException) AndAlso
                                       {5, 32, 33}.Contains(ex.HResult And &HFFFF)
                Dim entry As New FileInfo(fileName)
                If entry.Exists AndAlso entry.IsReadOnly Then Throw New IOException("The staged VR bridge is read-only. Preserving the file and recovery lease: " & fileName, ex)
                If timer.Elapsed >= TimeSpan.FromSeconds(8) Then Throw New IOException("Windows is still denying removal of the staged VR bridge. The recovery lease was kept: " & fileName, ex)
                If Not retried Then Record(id, "Waiting for Windows to release the verified staged bridge.")
                retried = True
                Thread.Sleep(200)
            End Try
        Loop
    End Sub

    Private Sub RemoveStageTemp(fileName As String, expected As String, id As String)
        If Not File.Exists(fileName) Then Return
        If FileHash(fileName) = expected Then
            File.Delete(fileName)
        Else
            Record(id, "Kept incomplete or independently changed staging file " & fileName)
        End If
    End Sub

    Private Function RestoreLease(folder As String) As Boolean
        Dim leaseFile = Path.Combine(folder, LeaseName)
        If Not File.Exists(leaseFile) Then Return False
        If File.Exists(Path.Combine(folder, BenchmarkLease)) Then Throw New IOException("A benchmark lease also exists. Preserving both sessions.")
        EnsureIdle(folder)
        Dim info = JsonSerializer.Deserialize(Of Lease)(File.ReadAllBytes(leaseFile))
        If info Is Nothing OrElse Not Regex.IsMatch(If(info.Id, ""), "\A[0-9a-f]{32}\z") OrElse
           Not Regex.IsMatch(If(info.BridgeHash, ""), "\A[0-9a-f]{64}\z") OrElse
           Not String.Equals(info.GameFolder, folder, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Invalid VR play lease. The game files were preserved.")
        Dim saved = SessionFolder(info.Id)
        If Not File.ReadAllBytes(Path.Combine(saved, "session.id")).SequenceEqual(Encoding.ASCII.GetBytes(info.Id)) Then Throw New IOException("Session identity mismatch. The game files were preserved.")
        Dim backup = Path.Combine(saved, "original-avx.exe")
        If FileHash(backup) <> StockSha256 Then Throw New IOException("The original AVX backup failed verification. The game files were preserved.")
        Dim prefix = Path.Combine(folder, "gasvr-play-" & info.Id)
        Dim executable = Path.Combine(folder, Avx)
        Select Case FileHash(executable)
            Case VrSha256
                Dim restoreTemp = prefix & ".restore.tmp"
                If File.Exists(restoreTemp) AndAlso FileHash(restoreTemp) <> StockSha256 Then
                    restoreTemp = prefix & ".restore-" & Guid.NewGuid().ToString("N") & ".tmp"
                End If
                If Not File.Exists(restoreTemp) Then WriteNew(restoreTemp, File.ReadAllBytes(backup))
                If FileHash(restoreTemp) <> StockSha256 Then Throw New IOException("The restore copy failed verification.")
                File.Replace(restoreTemp, executable, Nothing)
            Case StockSha256
                ' Staging stopped before the swap, or recovery already restored the executable.
            Case Else
                Throw New IOException("The installed game executable changed independently. The current file and backup were preserved.")
        End Select
        If FileHash(executable) <> StockSha256 Then Throw New IOException("Stock restoration could not be verified.")
        RemoveMatching(Path.Combine(folder, BridgeName), info.BridgeHash, info.Id)
        RemoveStageTemp(prefix & ".exe.tmp", VrSha256, info.Id)
        RemoveStageTemp(prefix & ".dll.tmp", info.BridgeHash, info.Id)
        RemoveStageTemp(prefix & ".restore.tmp", StockSha256, info.Id)
        Record(info.Id, "Restored and verified stock AVX " & StockSha256 & ".")
        File.Delete(leaseFile)
        File.Delete(Path.Combine(saved, "session.id"))
        File.Delete(backup)
        Return True
    End Function

    ' Windows returns the command line as a counted UNICODE_STRING in our
    ' process, including when this x64 supervisor queries the x86 renderer.
    <DllImport("ntdll.dll")>
    Private Function NtQueryInformationProcess(handle As SafeProcessHandle, informationClass As Integer,
                                              buffer As IntPtr, size As Integer, ByRef used As Integer) As Integer
    End Function

    <DllImport("shell32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function CommandLineToArgvW(command As String, ByRef count As Integer) As IntPtr
    End Function

    <DllImport("kernel32.dll")>
    Private Function LocalFree(address As IntPtr) As IntPtr
    End Function

    Private Function IsOurGame(proc As Process, folder As String, id As String) As Boolean
        Dim imageName = proc.MainModule?.FileName
        If String.IsNullOrEmpty(imageName) OrElse Not String.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase) Then Return False
        Dim size As Integer
        NtQueryInformationProcess(proc.SafeHandle, 60, IntPtr.Zero, 0, size)
        If size <= IntPtr.Size * 2 OrElse size > 131072 Then Return False
        Dim buffer = Marshal.AllocHGlobal(size)
        Try
            Dim used As Integer
            If NtQueryInformationProcess(proc.SafeHandle, 60, buffer, size, used) <> 0 Then Return False
            Dim length = CUShort(CInt(Marshal.ReadInt16(buffer)) And &HFFFF)
            Dim content = Marshal.ReadIntPtr(buffer, IntPtr.Size)
            Dim offset = content.ToInt64() - buffer.ToInt64()
            If length Mod 2 <> 0 OrElse offset < IntPtr.Size * 2 OrElse offset + length > used Then Return False
            Dim command = Marshal.PtrToStringUni(content, length \ 2)
            Dim count As Integer
            Dim args = CommandLineToArgvW(command, count)
            If args = IntPtr.Zero Then Return False
            Try
                For i = 1 To count - 1
                    If Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size)) = "-gasvrplay=" & id Then Return True
                Next
                Return False
            Finally
                LocalFree(args)
            End Try
        Finally
            Marshal.FreeHGlobal(buffer)
        End Try
    End Function

    Private Function Activated(id As String, requested As Date) As Boolean
        Dim log = Path.Combine(SessionFolder(id), "bridge.log")
        If Not File.Exists(log) OrElse File.GetLastWriteTimeUtc(log) < requested.AddSeconds(-2) Then Return False
        Using stream As New FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            Using lines As New StreamReader(stream)
                While Not lines.EndOfStream
                    If lines.ReadLine().Contains("native renderer configured:", StringComparison.Ordinal) Then Return True
                End While
            End Using
        End Using
        Return False
    End Function

    Private Function WaitForGame(folder As String, id As String, requested As Date) As Integer
        Dim watched As New Dictionary(Of Integer, Process)
        Dim renderer As Process = Nothing
        Dim clock = Stopwatch.StartNew()
        Dim exitedBeforeObservation As TimeSpan? = Nothing
        Try
            Do
                For Each name In {"GRIDAutosport", "GRIDAutosport_avx"}
                    For Each proc In Process.GetProcessesByName(name)
                        Try
                            If watched.ContainsKey(proc.Id) OrElse proc.StartTime.ToUniversalTime() < requested.AddSeconds(-2) OrElse
                               Not IsOurGame(proc, folder, id) Then Continue For
                            watched.Add(proc.Id, proc)
                            If name = "GRIDAutosport_avx" Then renderer = proc
                            Record(id, "Watching " & name & " PID " & proc.Id & ".")
                            proc = Nothing ' The dictionary now owns its process handle.
                        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Win32Exception
                            ' A short-lived Steam wrapper may disappear during enumeration.
                        Finally
                            proc?.Dispose()
                        End Try
                    Next
                Next
                If renderer IsNot Nothing AndAlso watched.Values.All(Function(p) p.HasExited) Then
                    For Each p In watched.Values
                        Record(id, $"PID {p.Id} exited 0x{p.ExitCode:X8}.")
                    Next
                    If renderer.ExitCode = 0 AndAlso Not Activated(id, requested) Then
                        Throw New IOException("GRID Autosport exited without activating its VR renderer. See " & SessionFolder(id))
                    End If
                    Return renderer.ExitCode
                End If
                If renderer Is Nothing AndAlso watched.Values.All(Function(p) p.HasExited) AndAlso
                   File.Exists(Path.Combine(SessionFolder(id), "bridge.log")) Then
                    If Not exitedBeforeObservation.HasValue Then exitedBeforeObservation = clock.Elapsed
                    If clock.Elapsed - exitedBeforeObservation.Value > TimeSpan.FromSeconds(3) Then
                        Throw New IOException("The VR renderer started and exited before its exit code could be observed. See " & SessionFolder(id))
                    End If
                Else
                    exitedBeforeObservation = Nothing
                End If
                If renderer Is Nothing AndAlso clock.Elapsed >= TimeSpan.FromSeconds(90) Then Throw New IOException("Steam did not start a matching GRID Autosport AVX VR session within 90 seconds. Close any open game before recovery.")
                Thread.Sleep(200)
            Loop
        Finally
            For Each p In watched.Values
                p.Dispose()
            Next
        End Try
    End Function
End Module
