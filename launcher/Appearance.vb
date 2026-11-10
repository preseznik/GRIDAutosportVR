' DiRT3VR launcher palette and owner-drawn tab strip, adapted for Autosport.
Imports System.Drawing
Imports System.Windows.Forms

Public Module LauncherAppearance
    Public Function Px(control As Control, value As Integer) As Integer
        Return CInt(value * control.DeviceDpi / 96.0)
    End Function
    Public ReadOnly Property Dark As Boolean
        Get
            Return Application.IsDarkModeEnabled
        End Get
    End Property
    Public ReadOnly Property Surface As Color
        Get
            Return If(Dark, Color.FromArgb(23, 24, 27), Color.FromArgb(248, 248, 249))
        End Get
    End Property
    Public ReadOnly Property Cell As Color
        Get
            Return If(Dark, Color.FromArgb(40, 41, 45), Color.White)
        End Get
    End Property
    Public ReadOnly Property Ink As Color
        Get
            Return If(Dark, Color.FromArgb(242, 242, 244), Color.FromArgb(33, 33, 37))
        End Get
    End Property
    Public ReadOnly Property Line As Color
        Get
            Return If(Dark, Color.FromArgb(93, 93, 100), Color.FromArgb(172, 172, 180))
        End Get
    End Property
    Public ReadOnly Property Accent As Color
        Get
            Return AccentFor(Dark)
        End Get
    End Property
    Public Function AccentFor(darkTheme As Boolean) As Color
        Return If(darkTheme, Color.FromArgb(255, 71, 79), Color.FromArgb(174, 16, 32))
    End Function
    Public Sub Apply(root As Control)
        Dim modern = Not SystemInformation.HighContrast
        If modern Then
            root.BackColor = Surface
            root.ForeColor = Ink
        End If
        Dim button = TryCast(root, Button)
        If button IsNot Nothing Then
            button.FlatStyle = If(modern, FlatStyle.Flat, FlatStyle.System)
            If modern Then
                button.BackColor = Cell
                button.FlatAppearance.BorderColor = Line
                button.FlatAppearance.MouseOverBackColor = If(Dark, Color.FromArgb(75, 38, 43), Color.FromArgb(255, 232, 235))
                button.FlatAppearance.MouseDownBackColor = If(Dark, Color.FromArgb(95, 39, 47), Color.FromArgb(255, 210, 216))
                button.Padding = New Padding(Px(root, 9), Px(root, 3), Px(root, 9), Px(root, 3))
            End If
        End If
        Dim strip = TryCast(root, ModernTabs)
        If strip IsNot Nothing Then strip.UseModernStyle = modern
        For Each child As Control In root.Controls
            Apply(child)
        Next
    End Sub
End Module

Public Class ModernTabs
    Inherits TabControl
    Private modern As Boolean
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property UseModernStyle As Boolean
        Get
            Return modern
        End Get
        Set(value As Boolean)
            If modern = value Then Return
            modern = value
            SetStyle(ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer, value)
            DrawMode = If(value, TabDrawMode.OwnerDrawFixed, TabDrawMode.Normal)
            Padding = If(value, New Point(Px(Me, 14), Px(Me, 9)), New Point(Px(Me, 6), Px(Me, 3)))
            Invalidate()
        End Set
    End Property
    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        If Not modern Then
            MyBase.OnPaint(e) : Return
        End If
        e.Graphics.Clear(LauncherAppearance.Surface)
        For index = 0 To TabCount - 1
            Dim state = If(index = SelectedIndex AndAlso Focused, DrawItemState.Focus, DrawItemState.None)
            OnDrawItem(New DrawItemEventArgs(e.Graphics, Font, GetTabRect(index), index, state))
        Next
        Using pen As New Pen(LauncherAppearance.Line)
            e.Graphics.DrawRectangle(pen, 0, DisplayRectangle.Top - 1, Width - 1, Height - DisplayRectangle.Top)
        End Using
    End Sub
    Protected Overrides Sub OnDrawItem(e As DrawItemEventArgs)
        If Not modern OrElse e.Index < 0 Then
            MyBase.OnDrawItem(e) : Return
        End If
        Dim selected = e.Index = SelectedIndex
        Using brush As New SolidBrush(LauncherAppearance.Surface)
            e.Graphics.FillRectangle(brush, e.Bounds)
        End Using
        TextRenderer.DrawText(e.Graphics, TabPages(e.Index).Text, Font, e.Bounds, If(selected, LauncherAppearance.Accent, LauncherAppearance.Ink), TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
        If selected Then
            Using pen As New Pen(LauncherAppearance.Accent, Px(Me, 3))
                e.Graphics.DrawLine(pen, e.Bounds.Left + 6, e.Bounds.Bottom - 2, e.Bounds.Right - 6, e.Bounds.Bottom - 2)
            End Using
        End If
        If Focused AndAlso selected Then e.DrawFocusRectangle()
    End Sub
End Class

