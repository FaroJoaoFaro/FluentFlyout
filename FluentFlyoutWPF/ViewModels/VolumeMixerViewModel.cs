// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FluentFlyoutWPF.ViewModels;

/// <summary>
/// ViewModel for the volume mixer, exposing master volume and per-application audio sessions.
/// </summary>
public partial class VolumeMixerViewModel : ObservableObject, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private MMDevice? _device;
    private DispatcherTimer? _pollTimer;
    private bool _isDeviceLost;

    [ObservableProperty]
    public partial float MasterVolume { get; set; }

    [ObservableProperty]
    public partial bool IsMasterMuted { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<AudioSessionModel> Sessions { get; } = [];
    public event EventHandler? SessionVolumeChanged;

    public VolumeMixerViewModel()
    {
        DeviceName = string.Empty;
        AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;

        AttachDevice(AudioDeviceMonitor.Instance.GetDefaultRenderDevice());

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    partial void OnIsExpandedChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue) return;
        RefreshSessions();
    }

    private void AttachDevice(MMDevice? device)
    {
        _device = device;

        if (_device == null)
        {
            DeviceName = string.Empty;
            MasterVolume = 0f;
            IsMasterMuted = false;
            ClearSessions();
            return;
        }

        DeviceName = _device.FriendlyName;
        SyncMasterFromDevice();
        RefreshSessions();
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        Logger.Info("Default render device changed, reattaching volume mixer");

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AttachDevice(AudioDeviceMonitor.Instance.GetDeviceById(e.DeviceId));
        });
    }

    private void OnSessionVolumeChanged(object? sender, EventArgs e)
    {
        SessionVolumeChanged?.Invoke(sender, e);
    }

    private void ClearSessions()
    {
        foreach (var session in Sessions)
            session.VolumeChanged -= OnSessionVolumeChanged;

        Sessions.Clear();
    }


    [RelayCommand]
    private void ToggleMasterMute() => IsMasterMuted = !IsMasterMuted;

    [RelayCommand]
    private void OpenVolumeMixer() => IsExpanded = !IsExpanded;

    partial void OnMasterVolumeChanged(float value)
    {
        if (_device == null) return;
        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
        }
        catch (COMException ex)
        {
            MarkDeviceLost(ex);
        }
        if (MasterVolume == 0f)
        {
            IsMasterMuted = true;
        }
        else
        {
            IsMasterMuted = false;
        }
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (_device == null) return;
        try
        {
            _device.AudioEndpointVolume.Mute = value;
        }
        catch (COMException ex)
        {
            MarkDeviceLost(ex);
        }
    }

    public bool TryAdjustMasterVolume(float delta)
    {
        if (_device == null) return false;

        MasterVolume = Math.Clamp(MasterVolume + delta, 0f, 1f);
        return true;
    }

    public bool TryAdjustSessionVolume(int processId, float delta)
    {
        var session = Sessions.FirstOrDefault(session => session.ProcessId == processId);

        if (session == null)
        {
            RefreshSessions();
            session = Sessions.FirstOrDefault(session => session.ProcessId == processId);
        }

        if (session == null) return false;

        try
        {
            session.AdjustVolume(delta);
            return true;
        }
        catch (COMException ex)
        {
            MarkDeviceLost(ex);
            return false;
        }
    }


    public void SyncMasterFromDevice()
    {
        if (_device == null) return;

        float vol;
        bool mute;
        try
        {
            vol = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            mute = _device.AudioEndpointVolume.Mute;
        }
        catch (COMException ex)
        {
            MarkDeviceLost(ex);
            return;
        }

        if (MathF.Abs(MasterVolume - vol) > 0.001f)
            MasterVolume = vol;

        if (IsMasterMuted != mute)
            IsMasterMuted = mute;
    }


    [RelayCommand]
    public void RefreshSessions()
    {
        ClearSessions();

        if (_device == null)
            return;

        try
        {
            // update device reference because previous _device doesn't have updated sessions
            var updatedDevice = AudioDeviceMonitor.Instance.GetDeviceById(_device.ID) ?? _device;
            var sessionManager = updatedDevice.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                AudioSessionState sessionState = session.State;
                if (sessionState == AudioSessionState.AudioSessionStateExpired) continue;

                int pid = (int)session.GetProcessID;

                string name = pid != 0 ? GetSessionDisplayName(session) : "System sounds";

                if (name == "FluentFlyout") continue;

                var icon = MediaPlayerData.GetAndCacheProcessIcon(pid, name);
                var audioSession = new AudioSessionModel(session, name, pid, sessionState, icon);
                audioSession.VolumeChanged += OnSessionVolumeChanged;
                Sessions.Add(audioSession);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate audio sessions");
        }
    }

    private static string GetSessionDisplayName(AudioSessionControl session)
    {
        if (!string.IsNullOrWhiteSpace(session.DisplayName))
            return session.DisplayName;

        try
        {
            uint pid = session.GetProcessID;
            if (pid != 0)
            {
                var process = Process.GetProcessById((int)pid);
                var mainModule = process.MainModule;

                if (mainModule != null)
                {
                    return !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription)
                    ? mainModule.FileVersionInfo.FileDescription
                    : process.MainWindowTitle;
                }
                else
                {
                    return process.MainWindowTitle is { Length: > 0 } title
                    ? title
                    : process.ProcessName;
                }
            }
        }
        catch
        {
            // Process may have exited
        }

        return "Unknown";
    }


    // device and session references stop working when the Windows Audio service restarts
    private void MarkDeviceLost(COMException ex)
    {
        if (_isDeviceLost) return;

        _isDeviceLost = true;
        Logger.Warn(ex, "Lost connection to audio device, reattaching volume mixer");
    }

    private void TryReattachDevice()
    {
        var device = AudioDeviceMonitor.Instance.GetDefaultRenderDevice();
        if (device == null) return;

        try
        {
            _ = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch (COMException)
        {
            return;
        }

        _isDeviceLost = false;
        AttachDevice(device);
        Logger.Info("Reattached volume mixer to audio device");
    }

    public void OnPollTick(object? sender, EventArgs e)
    {
        if (_isDeviceLost)
        {
            TryReattachDevice();
            return;
        }

        SyncMasterFromDevice();

        try
        {
            foreach (var session in Sessions)
            {
                session.SyncFromDevice();
                //Logger.Trace("Session '{0}' (PID {1}) - Volume: {2}, Muted: {3}, State: {4}",
                //    session.DisplayName, session.ProcessId, session.Volume, session.IsMuted, session.State);
            }
        }
        catch (COMException ex)
        {
            MarkDeviceLost(ex);
        }
    }


    public void Dispose()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Tick -= OnPollTick;
            _pollTimer.Stop();
            _pollTimer = null;
        }

        AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;

        ClearSessions();

        GC.SuppressFinalize(this);
    }
}