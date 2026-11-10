Imports System.Text.Json
Imports System.Text.Json.Nodes

Friend Module LauncherSettings
    Public ReadOnly Property SettingsPath As String
        Get
            Dim userData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            If String.IsNullOrWhiteSpace(userData) Then userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            Return Path.Combine(userData, "GRIDAutosportVR", "settings.json")
        End Get
    End Property

    Private Function ReadDocument(fileName As String) As JsonObject
        Try
            Dim document = TryCast(JsonNode.Parse(File.ReadAllText(fileName)), JsonObject)
            If document Is Nothing Then Throw New JsonException("Expected a JSON object in " & fileName)
            Return document
        Catch ex As FileNotFoundException
            Return New JsonObject()
        Catch ex As DirectoryNotFoundException
            Return New JsonObject()
        End Try
    End Function

    Public Function ReadHudFollowView(fileName As String) As Boolean
        Dim node As JsonNode = Nothing
        If Not ReadDocument(fileName).TryGetPropertyValue("hudFollowView", node) Then Return False
        Dim scalar = TryCast(node, JsonValue)
        Dim enabled As Boolean
        If scalar Is Nothing OrElse Not scalar.TryGetValue(enabled) Then Throw New JsonException("hudFollowView must be true or false in " & fileName)
        Return enabled
    End Function

    Public Sub SaveHudFollowView(fileName As String, enabled As Boolean)
        Dim document = ReadDocument(fileName)
        document("hudFollowView") = JsonValue.Create(enabled)
        Directory.CreateDirectory(Path.GetDirectoryName(fileName))
        Dim temporary = fileName & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, document.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & Environment.NewLine)
            File.Move(temporary, fileName, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub
End Module
