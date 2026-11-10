' Discovery from DiRT3VR's launcher/Core.vb.
Imports Microsoft.Win32
Imports System.Text.RegularExpressions

Friend Module SteamLibrary
    Private Function ClientRoots() As List(Of String)
        Dim roots As New List(Of String)
        Try
            Using key = Registry.CurrentUser.OpenSubKey("Software\Valve\Steam")
                Dim path = TryCast(key?.GetValue("SteamPath"), String)
                If Not String.IsNullOrWhiteSpace(path) Then roots.Add(path.Replace("/", "\"))
            End Using
            Using machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
                  key = machine.OpenSubKey("SOFTWARE\Valve\Steam")
                Dim path = TryCast(key?.GetValue("InstallPath"), String)
                If Not String.IsNullOrWhiteSpace(path) Then roots.Add(path)
            End Using
        Catch ex As Exception When TypeOf ex Is Security.SecurityException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Discovery is optional; default install and manual selection remain available.
        End Try
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"))
        Return roots
    End Function

    Public Function SteamClient() As String
        For Each root In ClientRoots().Distinct(StringComparer.OrdinalIgnoreCase)
            If Not Path.IsPathFullyQualified(root) Then Continue For
            Dim executable = Path.Combine(root, "steam.exe")
            If Not File.Exists(executable) Then Continue For
            Try
                Dim version = FileVersionInfo.GetVersionInfo(executable)
                If String.Equals(version.OriginalFilename, "steam.exe", StringComparison.OrdinalIgnoreCase) AndAlso
                    String.Equals(version.CompanyName, "Valve Corporation", StringComparison.OrdinalIgnoreCase) Then Return executable
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ComponentModel.Win32Exception
                ' Continue to the next registered Steam installation.
            End Try
        Next
        Return Nothing
    End Function

    Public Function SteamLibraries() As IEnumerable(Of String)
        Dim roots = ClientRoots()
        For Each root In roots.ToArray()
            Dim libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf")
            Try
                If File.Exists(libraries) Then
                    For Each match As Match In Regex.Matches(File.ReadAllText(libraries), """path""\s+""([^""]+)""")
                        roots.Add(match.Groups(1).Value.Replace("\\", "\"))
                    Next
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' A protected library listing must not prevent selecting a game manually.
            End Try
        Next
        Return roots.Distinct(StringComparer.OrdinalIgnoreCase)
    End Function

End Module
