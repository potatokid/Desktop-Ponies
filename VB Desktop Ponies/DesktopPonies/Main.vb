﻿Imports System.Globalization
Imports System.IO
Imports CSDesktopPonies.SpriteManagement

''' <summary>
''' This is the Main form that handles startup and pony selection.
''' </summary>
Public Class Main
    Friend Shared Instance As Main

#Region "Fields and Properties"
    Private initialized As Boolean
    Private loading As Boolean
    Private loadWatch As New Diagnostics.Stopwatch()
    Private ReadOnly idleWorker As IdleWorker = idleWorker.CurrentThreadWorker

    Private oldWindowState As FormWindowState
    Private layoutPendingFromRestore As Boolean

    Private animator As DesktopPonyAnimator
    Private ponyViewer As ISpriteCollectionView
    Private ReadOnly startupPonies As New List(Of Pony)
    Friend SelectablePonies As New List(Of PonyBase)
    Friend ReadOnly DeadEffects As New List(Of Effect)
    Friend ReadOnly ActiveSounds As New List(Of Object)
    Friend ReadOnly HouseBases As New List(Of HouseBase)
    Private screensaverForms As List(Of ScreensaverBackgroundForm)

    ''' <summary>
    ''' Are ponies currently walking around the desktop?
    ''' </summary>
    Friend PoniesHaveLaunched As Boolean

    Friend ReadOnly games As New List(Of Game)
    Friend CurrentGame As Game

    Private tempFilterOptions As String()

    Private preventLoadProfile As Boolean

    Private previewWindowRectangle As Func(Of Rectangle)

    Private ReadOnly selectionControlFilter As New Dictionary(Of PonySelectionControl, Boolean)
    Private ponyOffset As Integer
    Private ReadOnly selectionControlsFilteredVisible As IEnumerable(Of PonySelectionControl) =
        selectionControlFilter.Where(Function(kvp) kvp.Value).Select(Function(kvp) kvp.Key)
#End Region

    Friend Enum BehaviorOption
        Name = 1
        Probability = 2
        MaxDuration = 3
        MinDuration = 4
        Speed = 5 'specified in pixels per tick of the timer
        RightImagePath = 6
        LeftImagePath = 7
        MovementType = 8
        LinkedBehavior = 9
        SpeakingStart = 10
        SpeakingEnd = 11
        Skip = 12 'Should we skip this behavior when considering ones to randomly choose (part of an interaction/chain?)
        XCoord = 13  'used when following/moving to a point on the screen.
        YCoord = 14
        ObjectToFollow = 15
        AutoSelectImages = 16
        FollowStoppedBehavior = 17
        FollowMovingBehavior = 18
        RightImageCenter = 19
        LeftImageCenter = 20
        DoNotRepeatImageAnimations = 21
        Group = 22
    End Enum

#Region "Initialization"
    Public Sub New()
        loadWatch.Start()
        InitializeComponent()
        initialized = True
    End Sub

    Private Sub Main_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        BeginInvoke(New MethodInvoker(AddressOf LoadInternal))
    End Sub

    ''' <summary>
    ''' Read all configuration files and pony folders.
    ''' </summary>
    Private Sub LoadInternal()
        Console.WriteLine("Main Loading after {0:0.00s}", loadWatch.Elapsed.TotalSeconds)
        Instance = Me

        Text = "Desktop Ponies v" & My.MyApplication.GetProgramVersion()
        Me.Icon = My.Resources.Twilight

        Application.DoEvents()

        'DesktopHandle = DetectFulLScreen_m.GetDesktopWindow()
        'ShellHandle = DetectFulLScreen_m.GetShellWindow()



        Try
            Dim Arguments = My.Application.CommandLineArgs

            If Arguments.Count = 0 Then

                If Environment.GetCommandLineArgs()(0).EndsWith(".scr", StringComparison.OrdinalIgnoreCase) Then
                    'for some versions of windows, starting with no parameters is the same as /c (configure)
                    Reference.SetScreensaverPath()
                    Me.Close()
                    Exit Sub
                End If

                Exit Try
            End If

            'handle any comment line arguments
            If Arguments.Count > 0 Then
                Select Case Split(LCase(Trim(Arguments(0))), ":")(0)
                    Case "autostart"
                        Reference.AutoStarted = True
                        Me.ShowInTaskbar = False
                        ShowInTaskbar = False

                        Try
                            Options.LoadProfile("autostart")
                        Catch
                            Options.LoadDefaultProfile()
                        End Try

                        'windows is telling us "start as a screensaver"
                    Case "/s"
                        Dim path = Reference.TryGetScreensaverPath()
                        If path Is Nothing Then
                            MessageBox.Show(Me, "The screensaver path has not been configured correctly." &
                                            " Until it has been set, the screensaver mode cannot be used.",
                                            "Screensaver Not Configured", MessageBoxButtons.OK, MessageBoxIcon.Error)
                            Close()
                        End If

                        Options.InstallLocation = path
                        Reference.InScreensaverMode = True
                        Reference.AutoStarted = True
                        ShowInTaskbar = False
                        WindowState = FormWindowState.Minimized

                        Try
                            Options.LoadProfile("screensaver")
                        Catch
                            Options.LoadDefaultProfile()
                        End Try

                        'windows says: "preview screensaver".  This isn't implemented so just quit
                    Case "/p"
                        Me.Close()
                        Exit Sub
                        'windows says:  "configure screensaver"
                    Case "/c"
                        Reference.SetScreensaverPath()
                        Me.Close()
                        Exit Sub
                    Case Else
                        MsgBox("Invalid command line argument.  Usage: " & ControlChars.NewLine & _
                               "desktop ponies.exe autostart - Automatically start with saved settings (or defaults if no settings are saved)" & ControlChars.NewLine & _
                               "desktop ponies.exe /s - Start in screensaver mode (you need to run /c first to configure the path to the pony files)" & ControlChars.NewLine & _
                               "desktop ponies.exe /c - Configure the path to pony files, only used for Screensaver mode." & ControlChars.NewLine & _
                               "desktop ponies.exe /p - Screensaver preview use only.  Not implemented.")
                        Me.Close()
                        Exit Sub
                End Select
            End If

        Catch ex As Exception
            My.Application.NotifyUserOfNonFatalException(ex, "Error processing command line arguments. They will be ignored.")
        End Try

        loading = True

        If Not Reference.AutoStarted Then
            WindowState = FormWindowState.Normal
        End If

        'temporarily save filter selections, if any, in the case that we are reloading after making a change in the editor.
        '(Loading options resets the filter, and will cause havoc otherwise)
        SaveFilterSelections()
        LoadFilterSelections()

        ' Load the profile that was last in use by this user.
        Dim profile = Options.DefaultProfileName
        Dim profileFile As IO.StreamReader = Nothing
        Try
            profileFile = New IO.StreamReader(IO.Path.Combine(Options.ProfileDirectory, "current.txt"),
                                              System.Text.Encoding.UTF8)
            profile = profileFile.ReadLine()
        Catch ex As IO.FileNotFoundException
            ' We don't mind if no preferred profile is saved.
        Catch ex As IO.DirectoryNotFoundException
            ' In screensaver mode, the user might set a bad path. We'll ignore it for now.
        Finally
            If profileFile IsNot Nothing Then profileFile.Close()
        End Try
        GetProfiles(profile)

        Dim loadTemplates = True
        Dim startedAsScr = Environment.GetCommandLineArgs()(0).EndsWith(".scr", StringComparison.OrdinalIgnoreCase)
        If startedAsScr Then
            Dim screensaverPath = Reference.TryGetScreensaverPath()
            If screensaverPath Is Nothing Then
                MessageBox.Show(
                    Me, "The screensaver has not yet been configured, or the previous configuration is invalid. Please reconfigure now.",
                    "Configuration Missing", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Dim result = Reference.SetScreensaverPath()
                If Not result Then
                    MessageBox.Show(Me, "You will be unable to run Desktop Ponies as a screensaver until it is configured.",
                                    "Configuration Aborted", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    MessageBox.Show(Me, "Restart Desktop Ponies for the new settings to take effect.",
                                    "Configuration Successful", MessageBoxButtons.OK, MessageBoxIcon.Information)
                End If
                loadTemplates = False
                loading = False
                Close()
            End If
            ' Start in a minimized state to load, and attempt to open the screensaver profile.
            ShowInTaskbar = False
            WindowState = FormWindowState.Minimized
            Try
                Options.LoadProfile("screensaver")
            Catch
                Options.LoadDefaultProfile()
            End Try
        End If

        ' Force any pending messages to be processed for Mono, which may get caught up with the background loading before the form gets
        ' fully drawn.
        Application.DoEvents()
        Console.WriteLine("Main Loaded after {0:0.00s}", loadWatch.Elapsed.TotalSeconds)

        If loadTemplates Then
            Threading.ThreadPool.QueueUserWorkItem(Sub() Me.LoadTemplates())
        End If
    End Sub

    Private Sub SaveFilterSelections()
        tempFilterOptions = FilterOptionsBox.CheckedItems.Cast(Of String).ToArray()
    End Sub

    Private Sub LoadFilterSelections()
        For Each item In tempFilterOptions
            Try
                FilterOptionsBox.SetItemChecked(FilterOptionsBox.Items.IndexOf(item), True)
            Catch ex As Exception
                ' Filter is not valid at time of restoring.  Do nothing.
            End Try
        Next
    End Sub

    Private Sub LoadTemplates()
        Dim ponyBaseDirectories = Directory.GetDirectories(Path.Combine(Options.InstallLocation, PonyBase.RootDirectory))
        Array.Sort(ponyBaseDirectories, StringComparer.CurrentCultureIgnoreCase)

        idleWorker.QueueTask(Sub() LoadingProgressBar.Maximum = ponyBaseDirectories.Count)

        Dim skipLoadingErrors As Boolean = False
        For Each folder In Directory.GetDirectories(Path.Combine(Options.InstallLocation, HouseBase.RootDirectory))
            skipLoadingErrors = LoadHouse(folder, skipLoadingErrors)
        Next

        Dim ponyBasesToAdd As New List(Of PonyBase)
        For Each folder In ponyBaseDirectories

            Dim pony = New PonyBase(folder)
            ponyBasesToAdd.Add(pony)
            idleWorker.QueueTask(Sub()
                                     Try
                                         AddToMenu(pony)
                                     Catch ex As InvalidDataException
                                         If skipLoadingErrors = False Then
                                             Select Case MsgBox("Error: Invalid data in " & PonyBase.ConfigFilename & " configuration file in " & folder _
                                                & ControlChars.NewLine & "Won't load this pony..." & ControlChars.NewLine _
                                                & "Do you want to skip seeing these errors?  Press No to see the error for each pony.  Press cancel to quit.", MsgBoxStyle.YesNoCancel)
                                                 Case MsgBoxResult.Yes
                                                     skipLoadingErrors = True
                                                 Case MsgBoxResult.No
                                                     'do nothing
                                                 Case MsgBoxResult.Cancel
                                                     Me.Close()
                                             End Select
                                         End If
                                     Catch ex As FileNotFoundException
                                         If skipLoadingErrors = False Then
                                             Select Case MsgBox("Error: No " & PonyBase.ConfigFilename & " configuration file found for folder: " & folder _
                                                & ControlChars.NewLine & "Won't load this pony..." & ControlChars.NewLine _
                                                & "Do you want to skip seeing these errors?  Press No to see the error for each pony.  Press cancel to quit.", MsgBoxStyle.YesNoCancel)
                                                 Case MsgBoxResult.Yes
                                                     skipLoadingErrors = True
                                                 Case MsgBoxResult.No
                                                     'do nothing
                                                 Case MsgBoxResult.Cancel
                                                     Me.Close()
                                             End Select
                                         End If
                                     End Try
                                     LoadingProgressBar.Value += 1
                                 End Sub)
        Next

        idleWorker.WaitOnAllTasks()
        If SelectablePonies.Count = 0 Then
            MessageBox.Show(Me, "Sorry, but you don't seem to have any ponies installed. " &
                            "There should have at least been a 'Derpy' folder in the same spot as this program.",
                            "No Ponies Found", MessageBoxButtons.OK, MessageBoxIcon.Information)
            GoButton.Enabled = False
        End If

        ' Load pony counts.
        idleWorker.QueueTask(Sub() Options.LoadPonyCounts())

        ' Load interactions, since references to other ponies can now be resolved.
        idleWorker.QueueTask(Sub()
                                 Try
                                     For Each pony In SelectablePonies
                                         pony.LoadInteractions()
                                     Next
                                 Catch ex As Exception
                                     My.Application.NotifyUserOfNonFatalException(ex, "There was a problem attempting to load interactions.")
                                 End Try
                             End Sub)

        ' Wait for all images to load.
        idleWorker.QueueTask(Sub()
                                 For Each control As PonySelectionControl In PonySelectionPanel.Controls
                                     control.ShowPonyImage = True
                                 Next
                             End Sub)

        idleWorker.QueueTask(Sub()
                                 Console.WriteLine("Templates Loaded after {0:0.00s}", loadWatch.Elapsed.TotalSeconds)

                                 If Reference.AutoStarted Then
                                     'Me.Opacity = 0
                                     GoButton_Click(Nothing, Nothing)
                                 Else
                                     'Me.Opacity = 100
                                 End If

                                 CountSelectedPonies()

                                 If OperatingSystemInfo.IsWindows Then LoadingProgressBar.Visible = False
                                 LoadingProgressBar.Value = 0
                                 LoadingProgressBar.Maximum = 1

                                 PoniesPerPage.Maximum = PonySelectionPanel.Controls.Count
                                 PonyPaginationLabel.Text = String.Format(
                                     CultureInfo.CurrentCulture, "Viewing {0} ponies", PonySelectionPanel.Controls.Count)
                                 PaginationEnabled.Enabled = True
                                 PaginationEnabled.Checked = OperatingSystemInfo.IsMacOSX

                                 PonySelectionPanel.Enabled = True
                                 SelectionControlsPanel.Enabled = True
                                 AnimationTimer.Enabled = True
                                 loading = False
                                 General.FullCollect()

                                 loadWatch.Stop()
                                 Console.WriteLine("Loaded in {0:0.00s} ({1} templates)",
                                                   loadWatch.Elapsed.TotalSeconds, PonySelectionPanel.Controls.Count)
                             End Sub)
    End Sub

    Private Function LoadHouse(folder As String, skipErrors As Boolean) As Boolean
        Try
            Dim base = New HouseBase(folder)
            HouseBases.Add(base)
            Return True
        Catch ex As Exception
            If skipErrors = False Then
                Select Case MsgBox("Error: No " & HouseBase.ConfigFilename & " configuration file found for folder: " & folder _
                   & ControlChars.NewLine & "Won't load this house/structure..." & ControlChars.NewLine _
                   & "Do you want to skip seeing these errors?  Press No to see the error for each folder.  Press cancel to quit.",
                   MsgBoxStyle.YesNoCancel)

                    Case MsgBoxResult.Yes
                        Return True
                    Case MsgBoxResult.No
                        'do nothing
                    Case MsgBoxResult.Cancel
                        Me.Close()
                End Select
            End If
            Return skipErrors
        End Try
    End Function

    Private Sub AddToMenu(ponyBase As PonyBase)
        SelectablePonies.Add(ponyBase)

        Dim ponySelection As New PonySelectionControl(ponyBase, ponyBase.Behaviors(0).RightImagePath, False)
        AddHandler ponySelection.PonyCount.TextChanged, AddressOf HandleCountChange
        If ponyBase.Directory = "Random Pony" Then
            ponySelection.NoDuplicates.Visible = True
            ponySelection.NoDuplicates.Checked = Options.NoRandomDuplicates
            AddHandler ponySelection.NoDuplicates.CheckedChanged, Sub() Options.NoRandomDuplicates = ponySelection.NoDuplicates.Checked
        End If
        If OperatingSystemInfo.IsMacOSX Then ponySelection.Visible = False

        selectionControlFilter.Add(ponySelection, ponySelection.Visible)
        PonySelectionPanel.Controls.Add(ponySelection)
        ponySelection.Update()
    End Sub

    Private Sub HandleCountChange(sender As Object, e As EventArgs)
        CountSelectedPonies()
    End Sub

    Private Sub CountSelectedPonies()

        Dim total_ponies As Integer = 0

        For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
            Dim count As Integer
            If Integer.TryParse(ponyPanel.PonyCount.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, count) Then
                total_ponies += count
            End If
        Next

        PonyCountValueLabel.Text = CStr(total_ponies)

    End Sub
#End Region

#Region "Selection"
    Private Sub ZeroPoniesButton_Click(sender As Object, e As EventArgs) Handles ZeroPoniesButton.Click
        For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
            ponyPanel.PonyCount.Text = "0"
        Next
    End Sub

    Private Sub SaveProfileButton_Click(sender As Object, e As EventArgs) Handles SaveProfileButton.Click
        Dim profileToSave = ProfileComboBox.Text

        If profileToSave = "" Then
            MsgBox("Enter a profile name first!")
            Exit Sub
        End If

        If String.Equals(profileToSave, Options.DefaultProfileName, StringComparison.OrdinalIgnoreCase) Then
            MsgBox("Cannot save over the '" & Options.DefaultProfileName & "' profile. " &
                   "To create a new profile, type a new name for the profile into the box. You will then be able to save the profile.")
            Exit Sub
        End If

        If Not ProfileComboBox.Items.Contains(profileToSave) Then
            ProfileComboBox.Items.Add(profileToSave)
        End If
        ProfileComboBox.SelectedItem = profileToSave

        Options.SaveProfile(profileToSave)
        MessageBox.Show(Me, "Profile '" & profileToSave & "' saved.", "Profile Saved",
                        MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub LoadProfileButton_Click(sender As Object, e As EventArgs) Handles LoadProfileButton.Click
        Options.LoadProfile(ProfileComboBox.Text)
    End Sub

    Private Sub OnePoniesButton_Click(sender As Object, e As EventArgs) Handles OnePoniesButton.Click
        For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
            ponyPanel.PonyCount.Text = "1"
        Next
    End Sub

    Private Sub OptionsButton_Click(sender As Object, e As EventArgs) Handles OptionsButton.Click
        Using form = New OptionsForm()
            form.ShowDialog(Me)
        End Using
    End Sub

    Private Sub PonyEditorButton_Click(sender As Object, e As EventArgs) Handles PonyEditorButton.Click

        Reference.InPreviewMode = True
        Me.Visible = False
        Using form = New PonyEditor()
            previewWindowRectangle = AddressOf form.GetPreviewWindowScreenRectangle
            form.ShowDialog(Me)

            PonyShutdown()

            Reference.InPreviewMode = False
            If Not Me.IsDisposed Then
                Me.Visible = True
            End If

            If form.ChangesMade Then
                ResetPonySelection()
                LoadingProgressBar.Visible = True
                '(We need to reload everything to account for anything changed while in the editor)
                Main_Load(Nothing, Nothing)
            End If
        End Using

    End Sub

    Friend Function GetPreviewWindowRectangle() As Rectangle
        Return previewWindowRectangle()
    End Function

    Private Sub GamesButton_Click(sender As Object, e As EventArgs) Handles GamesButton.Click
        Try
            games.Clear()
            Dim gameDirectories = Directory.GetDirectories(Path.Combine(Options.InstallLocation, Game.RootDirectory))

            For Each gameDirectory In gameDirectories
                Try
                    Dim config_file_name = Path.Combine(gameDirectory, Game.ConfigFilename)
                    Dim new_game As New Game(gameDirectory)
                    games.Add(new_game)
                Catch ex As Exception
                    My.Application.NotifyUserOfNonFatalException(ex, "Error loading game: " & gameDirectory)
                End Try
            Next

            Me.Visible = False
            If New GameSelectionForm().ShowDialog() = DialogResult.OK Then
                startupPonies.Clear()
                PonyStartup()
                CurrentGame.Setup()
                animator.Start()
            Else
                If Me.IsDisposed = False Then
                    Me.Visible = True
                End If
            End If
        Catch ex As Exception
            My.Application.NotifyUserOfNonFatalException(ex, "Error loading games.")
#If DEBUG Then
            Throw
#End If
        End Try
    End Sub

    Private Sub GetProfiles(profileToAttemptToLoad As String)
        ProfileComboBox.Items.Clear()
        ProfileComboBox.Items.Add(Options.DefaultProfileName)
        Dim profiles = Options.GetKnownProfiles()
        If profiles IsNot Nothing Then ProfileComboBox.Items.AddRange(profiles)
        Dim profileIndex = ProfileComboBox.Items.IndexOf(profileToAttemptToLoad)
        If profileIndex <> -1 Then ProfileComboBox.SelectedIndex = profileIndex
    End Sub

    Private Sub CopyProfileButton_Click(sender As Object, e As EventArgs) Handles CopyProfileButton.Click
        preventLoadProfile = True

        Dim copiedProfileName = InputBox("Enter name of new profile to copy to:")
        copiedProfileName = Trim(copiedProfileName)
        If copiedProfileName = "" Then
            MsgBox("Can't enter a blank profile name!  Try again.")
            Exit Sub
        End If

        If String.Equals(copiedProfileName, Options.DefaultProfileName, StringComparison.OrdinalIgnoreCase) Then
            MsgBox("Cannot copy over the '" & Options.DefaultProfileName & "' profile")
            Exit Sub
        End If

        Options.SaveProfile(copiedProfileName)
        GetProfiles(copiedProfileName)

        preventLoadProfile = False
    End Sub

    Private Sub DeleteProfileButton_Click(sender As Object, e As EventArgs) Handles DeleteProfileButton.Click
        If String.Equals(ProfileComboBox.Text, Options.DefaultProfileName, StringComparison.OrdinalIgnoreCase) Then
            MsgBox("Cannot delete the '" & Options.DefaultProfileName & "' profile")
            Exit Sub
        End If

        preventLoadProfile = True

        If Options.DeleteProfile(ProfileComboBox.Text) Then
            MsgBox("Profile Deleted", MsgBoxStyle.OkOnly, "Success")
        Else
            MsgBox("Error attempting to delete profile. It may have already been deleted", MsgBoxStyle.OkOnly, "Error")
        End If
        GetProfiles(Options.DefaultProfileName)

        preventLoadProfile = False
    End Sub

    Private Sub ProfileComboBox_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ProfileComboBox.SelectedIndexChanged
        If Not preventLoadProfile Then
            Options.LoadProfile(ProfileComboBox.Text)
        End If
    End Sub

    Private Sub FilterAnyRadio_CheckedChanged(sender As Object, e As EventArgs) Handles FilterAnyRadio.CheckedChanged
        If FilterAnyRadio.Checked Then
            FilterOptionsBox.Enabled = True
            RefilterSelection()
        End If
    End Sub

    Private Sub FilterExactlyRadio_CheckedChanged(sender As Object, e As EventArgs) Handles FilterExactlyRadio.CheckedChanged
        If FilterExactlyRadio.Checked Then
            FilterOptionsBox.Enabled = True
            RefilterSelection()
        End If
    End Sub

    Private Sub FilterAllRadio_CheckedChanged(sender As Object, e As EventArgs) Handles FilterAllRadio.CheckedChanged
        If FilterAllRadio.Checked AndAlso Me.Visible Then
            FilterOptionsBox.Enabled = False
            RefilterSelection()
        End If
    End Sub

    Private Sub RefilterSelection(Optional tags As IEnumerable(Of String) = Nothing)
        If tags Is Nothing Then tags = FilterOptionsBox.CheckedItems.Cast(Of String)()

        For Each selectionControl As PonySelectionControl In PonySelectionPanel.Controls
            'reshow all ponies in show all mode.
            If FilterAllRadio.Checked Then
                selectionControlFilter(selectionControl) = True
            End If

            'don't show ponies that don't have at least one of the desired tags in Show Any.. mode
            If FilterAnyRadio.Checked Then
                Dim visible = False
                For Each tag_to_show In tags
                    If selectionControl.PonyBase.Tags.Contains(tag_to_show) OrElse
                        (tag_to_show <> "Not Tagged" AndAlso selectionControl.PonyBase.Tags.Count = 0) Then
                        visible = True
                        Exit For
                    End If
                Next
                selectionControlFilter(selectionControl) = visible
            End If

            'don't show ponies that don't have all of the desired tags in Show Exactly.. mode
            If FilterExactlyRadio.Checked Then
                Dim visible = True
                For Each tag_to_show In tags
                    If Not (selectionControl.PonyBase.Tags.Contains(tag_to_show) OrElse
                        (tag_to_show <> "Not Tagged" AndAlso selectionControl.PonyBase.Tags.Count = 0)) Then
                        visible = False
                        Exit For
                    End If
                Next
                selectionControlFilter(selectionControl) = visible
            End If
        Next

        ponyOffset = 0
        RepaginateSelection()
    End Sub

    Private Sub RepaginateSelection()
        PonySelectionPanel.SuspendLayout()

        Dim localOffset = 0
        Dim visibleCount = 0
        For Each selectionControl As PonySelectionControl In PonySelectionPanel.Controls
            Dim makeVisible = False
            If Not PaginationEnabled.Checked Then
                ' If pagination is disabled, simply show/hide the control according to the current filter.
                makeVisible = selectionControlFilter(selectionControl)
            ElseIf selectionControlFilter(selectionControl) Then
                ' If pagination is enabled, we will show it if it is filtered visible and within the page range.
                makeVisible = localOffset >= ponyOffset AndAlso visibleCount < PoniesPerPage.Value
                localOffset += 1
            End If
            If makeVisible Then visibleCount += 1
            Dim visibleChanged = selectionControl.Visible <> makeVisible
            selectionControl.Visible = makeVisible
            ' Force an update on Mac to try and get visibility change applied.
            If OperatingSystemInfo.IsMacOSX AndAlso visibleChanged Then
                selectionControl.Invalidate()
                selectionControl.Update()
            End If
        Next

        ' Force an update on Mac to try and clear leftover graphics.
        If OperatingSystemInfo.IsMacOSX Then
            PonySelectionPanel.Invalidate()
            PonySelectionPanel.Update()
        End If

        PonySelectionPanel.ResumeLayout()

        If Not PaginationEnabled.Checked OrElse visibleCount = 0 Then
            PonyPaginationLabel.Text = String.Format(CultureInfo.CurrentCulture, "Viewing {0} ponies", visibleCount)
        Else
            PonyPaginationLabel.Text =
            String.Format(CultureInfo.CurrentCulture,
                          "Viewing {0} to {1} of {2} ponies",
                          ponyOffset + 1,
                          Math.Min(ponyOffset + PoniesPerPage.Value, selectionControlsFilteredVisible.Count),
                          selectionControlsFilteredVisible.Count)
        End If

        Dim min = ponyOffset = 0
        Dim max = ponyOffset >= selectionControlsFilteredVisible.Count - PoniesPerPage.Value
        FirstPageButton.Enabled = Not min
        PreviousPageButton.Enabled = Not min
        PreviousPonyButton.Enabled = Not min
        NextPonyButton.Enabled = Not max
        NextPageButton.Enabled = Not max
        LastPageButton.Enabled = Not max
    End Sub

    Private Sub Main_KeyPress(sender As Object, e As KeyPressEventArgs) Handles MyBase.KeyPress
        If ProfileComboBox.Focused Then Exit Sub

        If Char.IsLetter(e.KeyChar) Then
            e.Handled = True
            For Each selectionControl In selectionControlsFilteredVisible
                If selectionControl.PonyName.Text.Length > 0 Then
                    Dim compare = String.Compare(selectionControl.PonyName.Text(0), e.KeyChar, StringComparison.OrdinalIgnoreCase)
                    If compare = 0 Then
                        PonySelectionPanel.ScrollControlIntoView(selectionControl)
                        selectionControl.PonyCount.Focus()
                    End If
                    If compare >= 0 Then Exit For
                End If
            Next
        ElseIf e.KeyChar = "#" Then
#If DEBUG Then
            Using newEditor = New PonyEditorForm2()
                newEditor.ShowDialog(Me)
            End Using
#End If
        End If
    End Sub

    Private Sub FirstPageButton_Click(sender As Object, e As EventArgs) Handles FirstPageButton.Click
        ponyOffset = 0
        RepaginateSelection()
    End Sub

    Private Sub PreviousPageButton_Click(sender As Object, e As EventArgs) Handles PreviousPageButton.Click
        ponyOffset -= Math.Min(ponyOffset, CInt(PoniesPerPage.Value))
        RepaginateSelection()
    End Sub

    Private Sub PreviousPonyButton_Click(sender As Object, e As EventArgs) Handles PreviousPonyButton.Click
        ponyOffset -= Math.Min(ponyOffset, 1)
        RepaginateSelection()
    End Sub

    Private Sub NextPonyButton_Click(sender As Object, e As EventArgs) Handles NextPonyButton.Click
        ponyOffset += Math.Min(selectionControlsFilteredVisible.Count - CInt(PoniesPerPage.Value) - ponyOffset, 1)
        RepaginateSelection()
    End Sub

    Private Sub NextPageButton_Click(sender As Object, e As EventArgs) Handles NextPageButton.Click
        ponyOffset += Math.Min(selectionControlsFilteredVisible.Count - CInt(PoniesPerPage.Value) - ponyOffset, CInt(PoniesPerPage.Value))
        RepaginateSelection()
    End Sub

    Private Sub LastPageButton_Click(sender As Object, e As EventArgs) Handles LastPageButton.Click
        ponyOffset = selectionControlsFilteredVisible.Count - CInt(PoniesPerPage.Value)
        RepaginateSelection()
    End Sub

    Private Sub PoniesPerPage_ValueChanged(sender As Object, e As EventArgs) Handles PoniesPerPage.ValueChanged
        If initialized Then RepaginateSelection()
    End Sub

    Private Sub PaginationEnabled_CheckedChanged(sender As Object, e As EventArgs) Handles PaginationEnabled.CheckedChanged
        PonyPaginationPanel.Enabled = PaginationEnabled.Checked
        RepaginateSelection()
    End Sub

    Private Sub FilterOptionsBox_ItemCheck(sender As Object, e As ItemCheckEventArgs) Handles FilterOptionsBox.ItemCheck
        Dim tags = FilterOptionsBox.CheckedItems.Cast(Of String).ToList()
        If e.CurrentValue <> e.NewValue Then
            Dim changedTag = CStr(FilterOptionsBox.Items(e.Index))
            If e.NewValue = CheckState.Checked Then
                tags.Add(changedTag)
            Else
                tags.Remove(changedTag)
            End If
        End If
        RefilterSelection(tags)
    End Sub
#End Region

#Region "Pony Startup"
    Private Sub GoButton_Click(sender As Object, e As EventArgs) Handles GoButton.Click
        If PonyLoader.IsBusy Then
            MessageBox.Show(Me, "Already busy loading ponies. Cannot start any more at this time.",
                            "Busy", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Else
            loading = True
            SelectionControlsPanel.Enabled = False
            LoadingProgressBar.Visible = True
            loadWatch.Restart()
            PonyLoader.RunWorkerAsync()
        End If
    End Sub

    Private Sub PonyLoader_DoWork(sender As Object, e As System.ComponentModel.DoWorkEventArgs) Handles PonyLoader.DoWork
        Try
            ' Note down the number of each pony that is wanted.
            Dim totalPonies As Integer
            Dim ponyBasesWanted As New List(Of Tuple(Of String, Integer))()
            For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
                Dim ponyName = ponyPanel.PonyName.Text
                Dim count As Integer
                If Integer.TryParse(ponyPanel.PonyCount.Text, count) AndAlso count > 0 Then
                    ponyBasesWanted.Add(Tuple.Create(ponyName, count))
                    totalPonies += count
                End If
            Next

            If totalPonies = 0 Then
                If Reference.InScreensaverMode Then
                    ponyBasesWanted.Add(Tuple.Create("Random Pony", 1))
                    totalPonies = 1
                Else
                    MessageBox.Show("You haven't selected any ponies! Choose some ponies to roam your desktop first.",
                                    "No Ponies Selected", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    e.Cancel = True
                    Return
                End If
            End If

            If totalPonies > Options.MaxPonyCount Then
                MessageBox.Show(String.Format(
                                "Sorry you selected {1} ponies, which is more than the limit specified in the options menu.{0}" &
                                "Try choosing no more than {2} in total.{0}" &
                                "(or, you can increase the limit via the options menu)",
                                vbNewLine, totalPonies, Options.MaxPonyCount),
                            "Too Many Ponies", MessageBoxButtons.OK, MessageBoxIcon.Information)
                e.Cancel = True
                Return
            End If

            ' Create the initial set of ponies to start.
            startupPonies.Clear()
            Dim randomPoniesWanted As Integer
            For Each ponyBaseWanted In ponyBasesWanted
                Dim base = FindPonyBaseByDirectory(ponyBaseWanted.Item1)
                If base.Directory <> "Random Pony" Then
                    ' Add the designated amount of a given pony.
                    For i = 1 To ponyBaseWanted.Item2
                        startupPonies.Add(New Pony(base))
                    Next
                Else
                    randomPoniesWanted = ponyBaseWanted.Item2
                End If
            Next

            ' Add a random amount of ponies.
            If randomPoniesWanted > 0 Then
                Dim remainingPonyBases As New List(Of PonyBase)(
                    SelectablePonies.Where(Function(pb) pb.Directory <> "Random Pony"))
                If Options.NoRandomDuplicates Then
                    remainingPonyBases.RemoveAll(Function(pb) ponyBasesWanted.Any(Function(t) t.Item1 = pb.Directory))
                End If
                For i = 1 To randomPoniesWanted
                    If remainingPonyBases.Count = 0 Then Exit For
                    Dim index = Rng.Next(remainingPonyBases.Count)
                    startupPonies.Add(New Pony(remainingPonyBases(index)))
                    If Options.NoRandomDuplicates Then
                        remainingPonyBases.RemoveAt(index)
                    End If
                Next
            End If

            Try
                If Options.PonyInteractionsEnabled Then
                    InitializeInteractions()
                End If
            Catch ex As Exception
                My.Application.NotifyUserOfNonFatalException(ex, "Unable to initialize interactions.")
            End Try

            PonyStartup()
        Catch ex As Exception
#If Not Debug Then
            My.Application.NotifyUserOfNonFatalException(ex, "Error attempting to launch ponies.")
            e.Cancel = True
#Else
            Throw
#End If
        End Try
    End Sub

    Private Function FindPonyBaseByDirectory(directory As String) As PonyBase
        For Each base As PonyBase In SelectablePonies
            If base.Directory = directory Then
                Return base
            End If
        Next

        Return Nothing
    End Function

    ''' <summary>
    ''' After all of the ponies, and all of their interactions are loaded, we need to go through and see
    ''' which interactions can actually be used with which ponies are loaded, and see which ponies each 
    ''' interaction should interact with.
    ''' </summary>
    ''' <remarks></remarks>
    Private Sub InitializeInteractions()
        For Each pony In startupPonies
            pony.InitializeInteractions(startupPonies)
        Next
    End Sub

    Private Sub PonyStartup()
        If Reference.InScreensaverMode Then
            SmartInvoke(Sub()
                            If Options.ScreensaverStyle <> Options.ScreensaverBackgroundStyle.Transparent Then
                                screensaverForms = New List(Of ScreensaverBackgroundForm)()

                                Dim backgroundColor As Color = Color.Black
                                Dim backgroundImage As Image = Nothing
                                If Options.ScreensaverStyle = Options.ScreensaverBackgroundStyle.SolidColor Then
                                    backgroundColor = Color.FromArgb(255, Options.ScreensaverBackgroundColor)
                                End If
                                If Options.ScreensaverStyle = Options.ScreensaverBackgroundStyle.BackgroundImage Then
                                    Try
                                        backgroundImage = Image.FromFile(Options.ScreensaverBackgroundImagePath)
                                    Catch
                                        ' Image failed to load, so we'll fall back to a background color.
                                    End Try
                                End If

                                For Each monitor In Screen.AllScreens
                                    Dim screensaverBackground As New ScreensaverBackgroundForm()
                                    screensaverForms.Add(screensaverBackground)

                                    If backgroundImage IsNot Nothing Then
                                        screensaverBackground.BackgroundImage = backgroundImage
                                    Else
                                        screensaverBackground.BackColor = backgroundColor
                                    End If

                                    screensaverBackground.Size = monitor.Bounds.Size
                                    screensaverBackground.Location = monitor.Bounds.Location

                                    screensaverBackground.Show()
                                Next
                            End If
                            Cursor.Hide()
                        End Sub)
        End If

        AddHandler Microsoft.Win32.SystemEvents.DisplaySettingsChanged, AddressOf ReturnToMenuOnResolutionChange
        ponyViewer = Options.GetInterface()
        ponyViewer.Topmost = Options.AlwaysOnTop

        If Not Reference.InPreviewMode Then
            ' Get a collection of all images to be loaded.
            Dim images As New HashSet(Of String)(StringComparer.Ordinal)
            For Each pony In startupPonies
                For Each behavior In pony.Behaviors
                    images.Add(behavior.LeftImagePath)
                    images.Add(behavior.RightImagePath)
                    For Each effect In behavior.Effects
                        images.Add(effect.LeftImagePath)
                        images.Add(effect.RightImagePath)
                    Next
                Next
            Next
            For Each house In HouseBases
                images.Add(house.LeftImagePath)
            Next

            SmartInvoke(Sub()
                            LoadingProgressBar.Value = 0
                            LoadingProgressBar.Maximum = images.Count
                        End Sub)
            Dim imagesLoaded = 0
            Dim loaded = Sub(sender As Object, e As EventArgs)
                             imagesLoaded += 1
                             PonyLoader.ReportProgress(imagesLoaded)
                         End Sub
            ponyViewer.LoadImages(images, loaded)
        End If

        animator = New DesktopPonyAnimator(ponyViewer, startupPonies, OperatingSystemInfo.IsMacOSX)
        Pony.CurrentViewer = ponyViewer
        Pony.CurrentAnimator = animator
    End Sub

    Private Sub ReturnToMenuOnResolutionChange(sender As Object, e As EventArgs)
        If Not Disposing AndAlso Not IsDisposed Then
            PonyShutdown()
            Main.Instance.SmartInvoke(Sub()
                                          MessageBox.Show("You will be returned to the menu because your screen resolution has changed.",
                                                          "Resolution Changed - Desktop Ponies",
                                                          MessageBoxButtons.OK, MessageBoxIcon.Information)
                                          Main.Instance.Show()
                                      End Sub)
        End If
    End Sub

    Private Sub PonyLoader_ProgressChanged(sender As Object, e As System.ComponentModel.ProgressChangedEventArgs) Handles PonyLoader.ProgressChanged
        ' Lazy solution to invoking issues and deadlocking the main thread.
        If Not InvokeRequired Then
            LoadingProgressBar.Value = e.ProgressPercentage
        End If
    End Sub

    Private Sub PonyLoader_RunWorkerCompleted(sender As Object, e As System.ComponentModel.RunWorkerCompletedEventArgs) Handles PonyLoader.RunWorkerCompleted
        loading = False
        Dim totalImages = LoadingProgressBar.Maximum

        LoadingProgressBar.Value = 0
        LoadingProgressBar.Maximum = 1
        SelectionControlsPanel.Enabled = True
        If OperatingSystemInfo.IsWindows Then LoadingProgressBar.Visible = False

        Dim oldLoader = PonyLoader
        PonyLoader = New System.ComponentModel.BackgroundWorker() With {
            .Site = oldLoader.Site,
            .WorkerReportsProgress = oldLoader.WorkerReportsProgress,
            .WorkerSupportsCancellation = oldLoader.WorkerSupportsCancellation
        }
        oldLoader.Dispose()

        If Not e.Cancelled Then
            PoniesHaveLaunched = True
            TempSaveCounts()
            Visible = False
            animator.Start()
            loadWatch.Stop()
            Console.WriteLine("Loaded in {0:0.00s} ({1} images)", loadWatch.Elapsed.TotalSeconds, totalImages)
        End If
    End Sub
#End Region

    Private Sub PonySelectionPanel_Resize(sender As Object, e As EventArgs) Handles PonySelectionPanel.Resize
        ' If a horizontal scrollbar has appeared, renew the layout to forcibly remove it.
        If PonySelectionPanel.HorizontalScroll.Visible Then
            PonySelectionPanel.SuspendLayout()
            For Each selectionControl As PonySelectionControl In PonySelectionPanel.Controls
                selectionControl.Visible = False
            Next
            PonySelectionPanel.ResumeLayout()
            ' Perform a layout so cached positions are cleared, then restore visibility to its previous state.
            RepaginateSelection()
        End If
    End Sub

    Friend Sub PonyShutdown()
        If Not IsNothing(animator) Then animator.Finish()
        PoniesHaveLaunched = False
        If Not IsNothing(animator) Then animator.Clear()

        If Not IsNothing(CurrentGame) Then
            CurrentGame.CleanUp()
            CurrentGame = Nothing
        End If

        If screensaverForms IsNot Nothing Then
            For Each screensaverForm In screensaverForms
                screensaverForm.Dispose()
            Next
            screensaverForms = Nothing
        End If

        If Object.ReferenceEquals(animator, Pony.CurrentAnimator) Then
            Pony.CurrentAnimator = Nothing
        End If
        animator = Nothing

        If Not IsNothing(ponyViewer) Then
            ponyViewer.Close()
        End If

        RemoveHandler Microsoft.Win32.SystemEvents.DisplaySettingsChanged, AddressOf ReturnToMenuOnResolutionChange
    End Sub

    ''' <summary>
    ''' Save pony counts so they are preserved through clicking on and off filters.
    ''' </summary>
    Private Sub TempSaveCounts()
        If PonySelectionPanel.Controls.Count = 0 Then Exit Sub

        Options.PonyCounts.Clear()

        For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
            Dim count As Integer
            Integer.TryParse(ponyPanel.PonyCount.Text, count)
            Options.PonyCounts(ponyPanel.PonyBase.Directory) = count
        Next
    End Sub

    ''' <summary>
    ''' Resets pony selection related controls, which will require them to be reloaded from disk.
    ''' </summary>
    Private Sub ResetPonySelection()
        SelectablePonies.Clear()
        SelectionControlsPanel.Enabled = False
        selectionControlFilter.Clear()
        PonySelectionPanel.SuspendLayout()
        For Each ponyPanel As PonySelectionControl In PonySelectionPanel.Controls
            ponyPanel.Dispose()
        Next
        PonySelectionPanel.Controls.Clear()
        PonySelectionPanel.ResumeLayout()
    End Sub

    Friend Sub CleanupSounds()
        Dim soundsToRemove As LinkedList(Of Microsoft.DirectX.AudioVideoPlayback.Audio) = Nothing

        For Each sound As Microsoft.DirectX.AudioVideoPlayback.Audio In ActiveSounds
            If sound.State = Microsoft.DirectX.AudioVideoPlayback.StateFlags.Paused OrElse
                sound.CurrentPosition >= sound.Duration Then
                sound.Dispose()
                If soundsToRemove Is Nothing Then soundsToRemove = New LinkedList(Of Microsoft.DirectX.AudioVideoPlayback.Audio)
                soundsToRemove.AddLast(sound)
            End If
        Next

        If soundsToRemove IsNot Nothing Then
            For Each sound In soundsToRemove
                ActiveSounds.Remove(sound)
            Next
        End If
    End Sub

    Friend Sub ResetToDefaultFilterCategories()
        FilterOptionsBox.SuspendLayout()
        FilterOptionsBox.Items.Clear()
        FilterOptionsBox.Items.AddRange(
            {"Main Ponies",
             "Supporting Ponies",
             "Alternate Art",
             "Fillies",
             "Colts",
             "Pets",
             "Stallions",
             "Mares",
             "Alicorns",
             "Unicorns",
             "Pegasi",
             "Earth Ponies",
             "Non-Ponies",
             "Not Tagged"})
        FilterOptionsBox.ResumeLayout()
    End Sub

    Private Sub Main_LocationChanged(sender As Object, e As EventArgs) Handles MyBase.LocationChanged
        ' If we have just returned from the minimized state, the flow panel will have an incorrect scrollbar.
        ' Force a layout to get the bar re-evaluated and fixed.
        If oldWindowState = FormWindowState.Minimized AndAlso WindowState <> FormWindowState.Minimized Then
            layoutPendingFromRestore = True
        End If
        oldWindowState = WindowState
    End Sub

    Private Sub PonySelectionPanel_Paint(sender As Object, e As PaintEventArgs) Handles PonySelectionPanel.Paint
        If layoutPendingFromRestore Then
            PonySelectionPanel.PerformLayout()
            layoutPendingFromRestore = False
        End If
    End Sub

    Private Sub AnimationTimer_Tick(sender As Object, e As EventArgs) Handles AnimationTimer.Tick
        For Each selectionControl As PonySelectionControl In PonySelectionPanel.Controls
            selectionControl.AdvanceTimeIndex(TimeSpan.FromMilliseconds(AnimationTimer.Interval))
        Next
    End Sub

    Private Sub Main_VisibleChanged(sender As Object, e As EventArgs) Handles MyBase.VisibleChanged
        AnimationTimer.Enabled = Visible AndAlso Not loading
    End Sub

    Private Sub Main_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        e.Cancel = loading AndAlso Not My.Application.IsFaulted
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            RemoveHandler Microsoft.Win32.SystemEvents.DisplaySettingsChanged, AddressOf ReturnToMenuOnResolutionChange
            If disposing Then
                If components IsNot Nothing Then components.Dispose()
                If animator IsNot Nothing Then animator.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub
End Class
