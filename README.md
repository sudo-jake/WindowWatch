# WindowWatch

WindowWatch is a portable x64 Windows desktop app for capturing intermittent visual glitches. It uses Windows Graphics Capture to watch the window you select. It keeps recent full-resolution images in memory and writes local files when a trigger fires. Tested on Windows 11 x64.

## Capture an event

1. Open WindowWatch.exe, choose your app's window, and click **Start watching**. Click **Refresh list** if you opened the window after launching WindowWatch.
2. Reproduce the issue with the window restored. The capture follows the selected window when it moves or another window covers it. Minimizing the target pauses monitoring. Resizing it resets the history.
3. Click **Stop watching** to save the frames still in the rolling buffer. If an automatic event is in progress, stopping finishes that capture. Otherwise the folder name includes **Stopped-watching**. The log shows the number of frames saved or reports when Windows delivered none.
4. Click **View session** for a list of this run's captures, or **View last capture** to step through its frames with the arrow keys or slider. **Open folder** opens the current session in Explorer. The default capture root is **Documents\WindowWatch Captures**.

WindowWatch remembers your triggers, timing, sensitivity and capture folder for your Windows account. On launch it selects the last window you successfully watched. If you open the target app later, click **Refresh list**. A restarted app can be matched by process name and window title. If several windows match, choose the one you want. Click **Start watching** when you're ready to record.

WindowWatch groups captures by window title, then by the time you clicked **Start watching**. A new run gets a separate session under the same window title. Each session has an **index.html** and a subfolder per saved clip, with its own images and timestamps. The final buffer can overlap an earlier event.

Click the **X** to hide WindowWatch in the system tray. Monitoring and saves continue in the same session. Click its tray icon to reopen the window. To quit, right-click the icon and choose **Exit**. WindowWatch saves the current buffer and finishes pending writes before it exits.

**Manage captures** lists completed clips from the chosen capture root across windows and sessions. Select one clip, or use Ctrl or Shift to select several, then choose **Move to Recycle Bin**. Each clip's PNGs, timestamps and viewer move together. To recover a clip, use **Restore** in the Windows Recycle Bin and refresh the capture list.

The default buffer covers 700 ms before the event starts and 700 ms after it ends. For a pause this includes the pause itself. Nearby triggers share a folder. The last image preceding the requested interval is also retained when available, which is especially helpful when a static window suddenly flashes. Start monitoring before the action you want to investigate.

## Triggers

| Trigger | What activates it |
| --- | --- |
| Pause during motion | Several regularly changing images, followed by a gap of 120 ms to 2.5 seconds by default, then resumed motion. The gap must also be at least 2.8 times the recent median interval. Lower **Min. pause** for shorter hitches. |
| Brief flash | A visible change across at least 0.5% of the sampled window that returns close to its earlier appearance within 350 ms. Lower **Min. flash area** if only a small panel flickers. |
| Large redraw | A sudden change across at least 1% of the sampled window. Normal navigation can trigger this too. Raise **Min. redraw area** or turn off this trigger if routine page changes fill the folder. |

Images are captured at the cadence Windows supplies. Detection compares colors on a grid of up to 128 × 80 points. Saved PNGs retain the captured window resolution. A visually identical repaint, a tiny change between sample points, a frame Windows skips, and a stall in an already still window can go undetected. A reported pause measures observed motion and can include capture delays. Protected windows can produce blank images.

## Files in an event

- **index.html** opens the local frame viewer. Zero milliseconds marks the first detection or, for a **Stopped-watching** capture, the end of monitoring. The stop capture includes available recent frames, typically around the last 3.45 seconds of capture activity with the default settings, plus the preceding image when available.
- **0001.png**, **0002.png**, and subsequent files are consecutive distinct images. An A → B → A change keeps all three images.
- **timeline.csv** lists each saved image with time relative to the event, estimated UTC time, and the Windows capture timestamp in milliseconds. UTC uses a wall-clock reference taken when monitoring starts.
- **events.csv** lists the start and end of each detection. A flash spans its appearance and return. A pause spans the last motion before the gap and the first motion after it.
- **samples.csv** maps individual frames delivered by Windows within the capture interval to their PNG, including repeated images. Windows may deliver no new frame while a window is still.

The rolling image history is limited to 160 MiB, and an individual event to 256 MiB. Very large or rapidly changing windows can shorten the saved interval. The viewer notes memory truncation. PNGs are exported on a worker thread. If the disk writer falls behind, the status log reports missed events. Closing the app finishes queued writes. Interrupted or unsuccessful writes can leave a folder ending in **.partial**.

## Build from source

In VS Code, open the repository folder and press **Ctrl+Shift+B** to build. Use **Terminal → Run Task → Run WindowWatch** to launch the app or **Test WindowWatch** to run the desktop tests.

Run the included build script in 64-bit PowerShell on Windows 11 x64. It uses the installed .NET Framework C# compiler and Windows metadata.

```powershell
.\build.ps1
```

The script creates **WindowWatch.exe** beside the source. The executable has no package or installer dependencies beyond the Windows components it uses.

Run **tests\run.ps1** in an interactive Windows desktop session to exercise the detector and the window-selection/capture/export path. A small generated test window animates, pauses, flashes and changes color. Results and example captures are saved in a new temporary directory.
