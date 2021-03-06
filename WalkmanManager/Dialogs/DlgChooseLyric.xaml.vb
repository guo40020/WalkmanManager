﻿Imports System.Collections.ObjectModel
Imports System.Text.RegularExpressions
Imports WalkmanManager.CloudMusic
Imports LibVLCSharp.Shared
Imports MaterialDesignThemes.Wpf

Public Class DlgChooseLyric

    Dim _songInfo As Database.SongInfo
    Dim libVlc As LibVLC
    Dim MediaPlayer As MediaPlayer
    Dim cachedLyrics As New Dictionary(Of Integer, AnalyzedLyric)
    Dim lyricLastCheck As Integer
    Dim currentLyric As AnalyzedLyric
    Dim flgClosing As Boolean = False
    Dim flgPause As Boolean
    Dim _cancelled As String = True
    Dim _lrc As String
    Dim _preLoad As Integer
    Dim preLoadThread As Threading.Thread

    Public ReadOnly Property Cancelled As String
        Get
            Return _cancelled
        End Get
    End Property

    Public ReadOnly Property lrc As String
        Get
            Return _lrc
        End Get
    End Property

    Public Sub New(libv As LibVLC, mp As MediaPlayer, searchResults As IEnumerable(Of ThirdPartyCloudMusicApi.SearchResult), songInfo As Database.SongInfo, Optional preLoad As Integer = 3)

        ' 此调用是设计器所必需的。
        InitializeComponent()

        ' 在 InitializeComponent() 调用之后添加任何初始化。
        _preLoad = preLoad
        _songInfo = songInfo
        DialogHostDataGridSearchResults.DialogContent = New DlgProgress
        DataGridSearchResults.ItemsSource = New ObservableCollection(Of ThirdPartyCloudMusicApi.SearchResult)(searchResults)
        TextBlockTitle.Text = $"正在为 {songInfo.Title} 选择歌词"
        ' Init VLC
        libVlc = libv
        MediaPlayer = mp
        MediaPlayer.Media = New Media(libVlc, songInfo.Path, FromType.FromPath)
        MediaPlayer.Play()
        AddHandler MediaPlayer.TimeChanged, AddressOf MediaPlayer_TimeChange
        AddHandler MediaPlayer.Stopped, AddressOf MediaPlayer_Stopped
        AddHandler MediaPlayer.EndReached, AddressOf MediaPlayer_EndReached
        ' Init pre-load
        preLoadThread = New Threading.Thread(Sub()
                                                 Dim c As Integer = 0
                                                 Dim tpApi As New ThirdPartyCloudMusicApi
                                                 While c < searchResults.Count AndAlso c < _preLoad
                                                     Dim lyric = tpApi.GetLyric(searchResults(c).Id)
                                                     If IsNothing(lyric) Then
                                                         Continue While
                                                     End If
                                                     If cachedLyrics.Keys.Contains(searchResults(c).Id) Then
                                                         Continue While
                                                     End If
                                                     Dim analyzedLyric = AnalyzeLyric(lyric)
                                                     ' Double check to avoid error, may be useless, but magic, who knows
                                                     If cachedLyrics.Keys.Contains(searchResults(c).Id) Then
                                                         Continue While
                                                     End If
                                                     cachedLyrics.Add(c, analyzedLyric)
                                                     c += 1
                                                 End While
                                                 Dispatcher.Invoke(Sub()
                                                                       TextBlockPreLoadHint.Text = "预加载已完成"
                                                                       ProgressBarPreLoadHint.IsIndeterminate = False
                                                                   End Sub)
                                             End Sub)
        preLoadThread.IsBackground = True
        preLoadThread.Start()
    End Sub

    Private Sub DlgChooseLyric_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        TextTotalPlayTime.Content = MsToTime(MediaPlayer.Length)
        LabelSongName.Content = _songInfo.Title
        LabelSongArtist.Content = _songInfo.Artists
    End Sub

    Public Shared Function MsToTime(ms As Long) As String
        Dim min = ms \ 1000 \ 60
        Dim sec = ms \ 1000 - min * 60
        Dim milli = ms - sec * 1000 - min * 60 * 1000
        Return String.Format("{0:D2}:{1:D2}.{2:D3}", min, sec, milli)
    End Function

    Public Shared Function TimeToMs(time As String) As Long
        Dim pattern = "([0-9]*):([0-9]{1,2}).([0-9]{1,3})"
        Dim m = Regex.Match(time, pattern)
        Dim min As Integer = m.Groups(1).ToString()
        Dim sec As Integer = m.Groups(2).ToString()
        Dim milli As Integer = m.Groups(3).ToString()
        Dim result As Long = (min * 60 + sec) * 1000 + milli
        Return result
    End Function

    Class AnalyzedLyric
        Public LyricDic As Dictionary(Of Long, String)
        Public Original As String

        Public Sub New()
            LyricDic = New Dictionary(Of Long, String)
        End Sub

        Public Sub Add(ms As Long, lyric As String)
            Try
                LyricDic.Add(ms, lyric)
            Catch ex As ArgumentException

            End Try
        End Sub

        Public ReadOnly Property Length() As Integer
            Get
                Return LyricDic.Count
            End Get
        End Property

        Public Function FindLyricLine(pos As Long) As String
            If LyricDic.Count = 0 Then
                Return ""
            End If
            For i As Integer = 0 To LyricDic.Count - 1
                If LyricDic.Keys(i) > pos Then
                    If i <> 0 Then
                        Return LyricDic(LyricDic.Keys(i - 1))
                    Else
                        Return LyricDic(LyricDic.Keys(i))
                    End If
                End If
            Next
            Return LyricDic(LyricDic.Keys(LyricDic.Count - 1))
        End Function
    End Class

    Private Function AnalyzeLyric(lyric As String) As AnalyzedLyric
        Dim lines = lyric.Split(vbLf)
        Dim pattern = "\[([0-9]*):([0-9]{1,2}).([0-9]{1,3})\]"
        Dim aLyric As New AnalyzedLyric
        aLyric.Original = lyric
        For Each line As String In lines
            ' Test if it is lyric line
            Dim m = Regex.Match(line, pattern)
            If m.Success And m.Index = 0 Then
                ' Yes it is
                Dim ms = TimeToMs(m.Value)
                aLyric.Add(ms, Regex.Replace(line, pattern, ""))
            End If
        Next
        Return aLyric
    End Function

    Private Sub ButtonClose_Click(sender As Object, e As RoutedEventArgs)
        flgClosing = True
        MediaPlayer.Stop()
        If preLoadThread.IsAlive Then
            preLoadThread.Abort()
        End If
    End Sub

    Private Async Sub DataGridSearchResults_MouseDoubleClick(sender As Object, e As MouseButtonEventArgs) Handles DataGridSearchResults.MouseDoubleClick
        If DataGridSearchResults.SelectedIndex <> -1 AndAlso Not cachedLyrics.Keys.Contains(DataGridSearchResults.SelectedIndex) Then
            ' Get lyric
            Dim tyApi As New ThirdPartyCloudMusicApi
            Dim lyric As String = Nothing
            Dim id = DataGridSearchResults.SelectedItem.Id
            DialogHostDataGridSearchResults.IsOpen = True
            Await Task.Run(Sub()
                               lyric = tyApi.GetLyric(id)
                           End Sub)
            DialogHostDataGridSearchResults.IsOpen = False
            If IsNothing(lyric) Then
                Await DialogHostDataGridSearchResults.ShowDialog(New DlgMessageDialog("", "加载失败"))
                DialogHostDataGridSearchResults.DialogContent = New DlgProgress
                Exit Sub
            End If
            TextBoxLyrics.Text = lyric
            Dim aLyric = AnalyzeLyric(lyric)
            cachedLyrics.Add(DataGridSearchResults.SelectedIndex, aLyric)
            currentLyric = aLyric
        Else
            TextBoxLyrics.Text = cachedLyrics(DataGridSearchResults.SelectedIndex).Original
            currentLyric = cachedLyrics(DataGridSearchResults.SelectedIndex)
        End If
    End Sub

    Private Sub MediaPlayer_TimeChange(sender As Object, e As MediaPlayerTimeChangedEventArgs)
        lyricLastCheck = My.Computer.Clock.TickCount
        Dispatcher.Invoke(Sub()
                              If Not IsNothing(currentLyric) Then
                                  LabelCurrentLyric.Content = currentLyric.FindLyricLine(e.Time)
                              End If
                              TextCurrentPlayTime.Content = MsToTime(e.Time)
                          End Sub)
    End Sub

    Private Sub MediaPlayer_EndReached(sender As Object, e As EventArgs)
        flgPause = False
    End Sub

    'MAGIC
    Private Sub MediaPlayer_Stopped(sender As Object, e As EventArgs)
        If Not flgClosing Then
            MediaPlayer.Play()
        End If
        flgPause = False
    End Sub

    Private Sub ButtonPlayPause_Click(sender As Object, e As RoutedEventArgs) Handles ButtonPlayPause.Click
        If MediaPlayer.IsPlaying Or flgPause Then
            MediaPlayer.Pause()
            flgPause = Not flgPause
        Else
            MediaPlayer.Play(New Media(libVlc, _songInfo.Path, FromType.FromPath))
        End If
    End Sub

    Private Sub ButtonDone_Click(sender As Object, e As RoutedEventArgs) Handles ButtonDone.Click
        _cancelled = False
        _lrc = currentLyric.Original
        If preLoadThread.IsAlive Then
            preLoadThread.Abort()
        End If
    End Sub

End Class
