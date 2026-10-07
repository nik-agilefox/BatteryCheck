# Battery Check — full guide

**English** · [Українська](GUIDE.uk.md) · [← README](../README.md)

Everything the app does, how it measures it and where the limits are. The short overview, install and build instructions are in the [README](../README.md).

- [Window](#window)
  - [Header](#header) · [Tiles](#tiles) · [Chart](#chart) · [Components](#components-card) · [Battery and discharge session](#battery-and-discharge-session) · [Footer](#footer)
- [Tabs](#tabs): [Processes](#processes) · [History](#history) · [Advice](#advice) · [Optimization](#optimization)
- [Power modes: Standard, Eco, Subzero](#power-modes-standard-eco-subzero)
- [On battery profile](#on-battery-profile) · [Before / after check](#before--after-check)
- [Idle baseline and extra watts](#idle-baseline-and-extra-watts)
- [NVIDIA wake log](#nvidia-wake-log) · [Who keeps the GPU awake](#who-keeps-the-gpu-awake)
- [Screen measurement (OLED)](#screen-measurement-oled)
- [Notifications](#notifications) · [Tray icon and background work](#tray-icon-and-background-work)
- [Desktop PCs without a battery](#desktop-pcs-without-a-battery)
- [Updates and installer](#updates-and-installer)
- [Command line](#command-line) · [Console version](#console-version)
- [Data sources](#data-sources) · [Log files](#log-files) · [Settings](#settings)
- [Source layout](#source-layout) · [Tests](#tests)

---

## Window

Light or dark theme follows Windows and switches on the fly. While the window is on screen the app polls once a second and uses about 3 % of one core; hidden in the tray or minimized it polls every 10 s and uses almost nothing — logging continues either way.

### Header

- **Title bar** — current watts and the power mode icon, e.g. `12❄ Battery Check`, so the taskbar button shows the draw at a glance (Windows 11 shows button labels only with *Combine taskbar buttons and hide labels → Never*).
- **⚡ Standard · 🍃 Eco · ❄ Subzero** — the power mode switch, see [Power modes](#power-modes-standard-eco-subzero).
- **EN | UA** — interface language. The window is rebuilt immediately; chart history and logs are kept. The tray menu, tooltips and the console version follow the choice. Numbers are formatted for the language: `23.4 W` / `23,4 Вт`.
- **Power state** — On battery, Charging, Plugged in, Critical charge (or *Desktop · no battery*).

### Tiles

- **Power draw** — on battery: the measured discharge, the 30 s average and, once the [idle baseline](#idle-baseline-and-extra-watts) is known, how many watts above idle you are now. The sparkline shows the last 5 minutes.
- **From the power adapter** (on AC) — an estimate: charge going into the battery + CPU + GPUs + “rest”, where “rest” (screen, board, storage) is the last value measured on battery. Windows does not report the adapter input — the standard AC adapter driver only says “connected”. Two lines show how much goes **to the battery** (measured) and **to the system**. The sparkline’s top is the charger rating: click it to set the exact rating from the adapter label (65–330 W) or leave *Estimate automatically* — then the lowest standard rating not below the highest input seen (a lower bound, so warning colors are off).
- **Charge** — percentage and energy left of the full charge capacity.
- **Remaining** — time to empty (on battery) or to full (charging) at the 30 s average power.
- **Battery health** — full charge capacity ÷ design capacity, wear and charge cycles; ⚠ below 80 %.

### Chart

Power over time with a hover tooltip (crosshair, all values at that moment). Legend and value labels at the right edge are in the color of their line.

- **Period** — 1 min, 5 min, 10 min, 30 min, 1 h, 3 h, 10 h (remembered). The window keeps 10 hours in memory while the app runs.
- **Mouse wheel** zooms one step; **dragging** moves into the past (the axis switches to clock time); **Back to now** or a **double-click** returns to live data.
- When there are more points than pixels, each pixel column keeps its minimum and maximum, so peaks are not lost. Long periods redraw less often: at most once per (period / 600).
- **Battery charge** is drawn as a faint background fill with its own 0–100 % scale (named in the legend, exact value in the tooltip) so it is not confused with watts.

In the **Components** view the lines do not overlap: **CPU (without graphics)** = whole package − integrated graphics; **Integrated graphics**; **Discrete GPU** (NVIDIA); **Rest** = battery − CPU − GPUs (screen, SSD, Wi-Fi, board, converter losses); **Total** — their sum: on battery the measured discharge, on AC an estimate without the power going into the battery. If the NVIDIA GPU is awake but not polled, Rest is not drawn — otherwise the GPU’s watts would appear as a jump in Rest.

### Components card

Now and 10 s average for the CPU (with the cores line), integrated graphics, the NVIDIA GPU (with P-state and load, *asleep* when in D3), the screen (after a [screen measurement](#screen-measurement-oled)) and Other/Rest.

Under the table:
- **Cores in deepest sleep** — share of time the cores spend in their deepest idle state (Windows “% C3 Time”, 10 s average). Higher is better at idle.
- **System timer** — how often Windows ticks. 15.6 ms is normal; **1 ms or less (⚠)** means some app asked Windows to wake up a thousand times a second, and the chip cannot sleep deeply. Windows does not say which app without administrator rights (`powercfg /energy` does). Eco and efficiency mode make Windows ignore such requests from background apps; Subzero also stops the vendor services that hold the timer.

Header buttons: **NVIDIA: N×/h** opens the [wake log](#nvidia-wake-log); **Measure screen** starts the [screen measurement](#screen-measurement-oled).

### Battery and discharge session

**Battery** — design and full charge capacity, wear, cycles, voltage, current, connected displays (and how many run through the NVIDIA GPU — a display on it keeps it awake).

**Discharge session** — since the charger was unplugged (or **Reset**). Energy is counted two independent ways: **by power** (sum of P·Δt) and **by remaining charge** (the drop reported by the battery controller). A mismatch of more than a few percent means the controller is poorly calibrated. Gaps longer than the polling interval (sleep, hibernation) are not integrated. After sleep on battery the card shows how long it slept and what it cost (average power in the tooltip).

### Footer

**Process analysis: on/off** (background app data for Advice), the status line (polling interval, log file, messages), **Check updates**, **NVIDIA polling: on/off**, **Log folder**.

---

## Tabs

### Processes

The chart shows the 5 processes with the highest average power over the visible period plus *Other processes*; the table — power now and average, CPU and GPU load. Processes with the same name are summed; a color stays with its process, not with its rank.

Windows does not measure per-process power, so this is an **estimate**:
- CPU core power (RAPL PP0) is split by CPU cycles used by each process;
- integrated graphics (RAPL PP1) and NVIDIA (NVML) — by each process’s load on their engines (“GPU Engine” counters; adapters identified through DXGI);
- if the NVIDIA GPU is awake but idle, its power is split between the processes **holding video memory** on it (column GPU shows *holds*) — see [Who keeps the GPU awake](#who-keeps-the-gpu-awake);
- **Unattributed** — what belongs to no process: on battery the screen, board, storage, network and the shared part of the CPU; on AC only the shared part of the CPU.

Sampling runs once a second while this view is on screen (~2 % of one core: `NtQuerySystemInformation` reads every thread, which is the only way to see `dwm`, `csrss` and services without administrator rights). Right-click a process to put it into efficiency mode on battery.

### History

Every past discharge (from unplugging to plugging in) from the CSV logs:

| Column | Meaning |
|---|---|
| date, time | start and end (*ongoing* for the current one) |
| on battery | time on battery while the app was logging; sleep and gaps are in the tooltip |
| charge | bar on a 0–100 % scale and `80 → 19 %` |
| where the energy went | **screen**, **idle** and apps that used **at least 1 Wh** (see below) |
| used | energy by the battery’s remaining capacity |
| avg power | average power while on battery |
| full discharge | how long the laptop would last from 100 % to 0 % at that average (discharges of 10 min or more) |
| indicator | charge indicator honesty (see below) |

The summary on top: number of discharges, total time on battery, energy, average power and the **typical runtime** (median of the full-discharge projections).

**Where the energy went:**
- **screen** — the image cost from the screen measurement × the share of white on screen, plus the panel itself × time with the screen on, if the panel could be measured;
- **idle** — battery counter − all apps − screen: the board, memory, SSD, Wi-Fi, the shared part of the CPU and the idle NVIDIA GPU — what is spent with any apps;
- **apps** — energy per process (as in [Processes](#processes)). Hover for the average power, how much was spent **with the app’s window active** and **in the background**, and the path of the exe (remembered in `logs\process_paths.csv`).

Logs of different versions and of copies running at the same time are merged by time. A gap over 60 s inside a discharge is sleep or the app not running (not counted as time on battery); a gap over 12 h starts a new discharge. Discharges under 2 min or 1 % are skipped. The tab re-reads the logs in the background when opened and once a minute.

**Charge indicator honesty** — energy delivered by the counter (power × time) ÷ the drop of the remaining capacity Windows bases its percentage on, over the same continuous stretches. 1.00 — honest; 0.80 — the percentage drops 25 % faster, and 0 % comes when the battery has delivered only 80 % of what it promised. It is also computed per charge band 0–20 … 80–100 % (in the tooltip), because at low charge the voltage is lower and a percent holds less energy. Verdicts:
- **honest** — over discharges of about 7 % (5 Wh) or more;
- **⚠ drops faster** — in 2 of the last 3 discharges the ratio is below 0.92;
- **⚠ drifting** — in some band the last 3 discharges are 7 % below that band’s usual value (median of earlier ones, needs at least 6 discharges);
- **jumps (↯)** — the remaining capacity fell by 3 % or more between two samples while three times less energy was delivered.

On a warning — a hint to calibrate (full charge → run down until it shuts off → full charge) or that the cells are wearing.

### Advice

Recommendations from built-in rules (no network, no external services) over the last 7 days on battery, sorted by benefit. Each shows the saving in watts, the extra minutes on a full battery and its basis (*measured*, *estimate from logs*, *typical value*). Each has a **Check** button that starts a [before / after check](#before--after-check) with its name.

- **Apps with unusual background draw.** First compared with **this app’s own usual value on this laptop**: background Wh per day ÷ hours on battery that day, median over days in the 30 days before the current week when the app ran and the laptop was on battery for at least 15 minutes; at least 3 such days. Advice when this week is above 1.5 × usual and above usual + 0.3 W (“Telegram uses 1.2 W in the background — usually 0.4 W on this laptop”). Until there is an own value — the category guideline (messenger up to ~0.3 W, browser ~1 W, web app ~0.5 W, game launcher and vendor utility ~0.2 W…).
- **NVIDIA wake-ups** — from the wake log, with likely culprits among running apps.
- **Screen** — refresh rate above 60 Hz (only if the panel offers a lower rate in the current resolution); on OLED, after the screen measurement — high brightness with bright content, lots of white on screen.
- **System** — Windows power mode on battery, external monitor on the NVIDIA GPU, a fast system timer, a discrete GPU disabled in Device Manager, too little data.

**While you are away** — average power of apps in **quiet minutes**: on battery, with no keyboard or mouse input for the whole minute. An app above 0.5 W in such minutes gets its own advice (Windows parts like System and dwm do not).

**What used the battery** — total, share, average background power, the usual value (yours or the category’s) and *⚠ above typical*.

**Rules.** Built-in rules are in `src\Core\rules.json` (embedded at build time): app **categories** with names in both languages, background guideline, advice text, process names and prefixes; **vendors** (Acer, Lenovo, ASUS, Dell, HP, MSI, Razer, Samsung) — how to recognize them by the BIOS string, their utilities, a hint about their quiet mode; **GPU pollers** — apps known to wake the GPU; **efficiency exclusions**; **Subzero services**. Your own rules go into `rules.user.json` in the app folder next to `logs`, in the same format: entries with the same `id` replace built-in ones, new ones are added, lists are merged. An error in the file does not break the advice — the built-in rules keep working and the error is shown in the tab.

App data is collected in the background on battery (switch: **Process analysis** in the footer): a process snapshot every 10 s (~0.2 % of one core), energy per process (total, with its window active, on GPUs) written once a minute to `logs\process_energy_YYYYMMDD.csv`. Nothing is collected on AC.

### Optimization

Third-party Windows services — not Microsoft and not inside `svchost` (decided by the file’s publisher; vendor services live both in `System32` and `DriverStore`); security services are not listed. For each: process, state, **average draw on battery** over 7 days and **now**, ⚑ if it is a known GPU poller. The heaviest on battery are on top.

The tab only shows them: **Open Services** → right-click → Stop (the service starts again after a reboot), or **Check** to measure the difference with a [before / after check](#before--after-check). Stopping the vendor services in one click is what [Subzero](#power-modes-standard-eco-subzero) does.

---

## Power modes: Standard, Eco, Subzero

The switch in the header. The mode is remembered; the accent color shows which one is on.

**⚡ Standard** — nothing is changed.

**🍃 Eco** — on battery:
- in the active power plan’s **battery values** (no administrator rights for a user plan): **processor boost off** (no frequency spikes — the main source of heat), **energy performance preference 100** for P- and E-cores, **Windows Energy saver at any charge**;
- **background apps in efficiency mode** (EcoQoS + low priority, re-applied every 10 s), except the active window with all processes of that app and their children (switch windows — it is lifted at once), Battery Check itself, the Windows shell and the exclusions in `rules.json` (players, call apps, assistants). Processes that were already in efficiency mode are left alone;
- **apps found on the discrete GPU** are switched to the integrated graphics from their next start (Settings → Display → Graphics, `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`); the previous values are restored when Eco is off.

Plugged in, nothing changes. Switching back to Standard restores the previous values — except the ones you changed yourself in the meantime. Every change goes to `logs\actions.csv`.

**❄ Subzero** — Eco plus:
- a **CPU cap**: maximum processor state 60 % for P- and E-cores on battery;
- **stopping vendor background services** listed in `rules.json` (`subzero_services`; for Acer — the Quick Access, Care Center and diagnostics agents, which hold the 1 ms system timer). One UAC prompt per switch; the command is passed as a single encoded PowerShell command, the result is read back from a temp file. The startup type is not changed, so after a reboot the services start as usual and the mode falls back to Eco (the app tells you). Switching away from Subzero starts them again.

On a desktop PC without a battery the modes apply all the time and write the **AC** values of the power plan; apps are not moved to the integrated graphics there.

---

## On battery profile

A card in the Advice tab. Applied 5 s after unplugging (connector bounce is not a reason), reverted when plugged in:
- **Brightness on battery** — *don’t change* or 30–70 %. Lowered only if it is higher now; restored on plug-in **only if you did not change it yourself** meanwhile (otherwise yours is kept and logged).
- **Refresh rate on battery** — the lowest rate the driver offers in the current resolution, changed without writing to the registry (a crash or reboot restores the usual mode). If the panel has a single rate, the card says so.
- **Windows power mode on battery** — the same four options as Settings → System → Power. This one is a permanent Windows setting and is not reverted on plug-in.
- **Efficiency mode on battery** — a list of apps (add: right-click in Processes or *What used the battery*, or the button of an app advice; remove: ×). On unplugging they get EcoQoS and low priority, like *Efficiency mode* in Task Manager; new processes of the same apps (browser tabs, restarts) are picked up every 30 s. Without administrator rights this works only for the user’s own processes — services and elevated apps are reported as needing admin. It lowers CPU use but **does not stop GPU wake-ups**.

The status line shows what is active (“brightness 80 → 50 %”) with **Undo now**. The profile state is kept in the registry, so after a crash everything is reverted on the next start on AC.

## Before / after check

A card at the top of the Advice tab: type what you change (“closed PredatorSense”) and **Start** — or press **Check** on any advice or service.

1. **Before** — 8 quiet minutes. If the last minutes were quiet and continuous, they are taken right away.
2. Make the change and press **Done**.
3. **After** — 8 more quiet minutes.
4. Result: “−2.3 ± 0.6 W · 19.5 → 17.2 W · ≈ +35 min” with details — CPU, share of time the NVIDIA GPU was awake, its wake-ups per hour, number of minutes.

Only **comparable minutes** count: all samples on battery, no keyboard or mouse input for at least 60 s, the same brightness, the app window on screen (otherwise polling and drawing differ), no screen measurement running. The uncertainty is the standard errors of the per-minute means added in quadrature; an effect counts if the difference is above two uncertainties and at least 0.3 W, otherwise *no difference found*. The screen is kept on during the check. Without 5 quiet minutes per phase within 30 minutes the check stops. Results go to `logs\experiments.csv`; the last five are shown under the card.

## Idle baseline and extra watts

On battery, the power tile shows **this machine’s idle draw** and how far above it you are: “idle 11.2 W · +8.3 W extra”. The tooltip breaks the extra down: **GPU awake** (its power — in quiet minutes it sleeps), **CPU above idle** × 1.75 (every CPU watt adds ~0.75 W to the rest: power conversion, memory), **screen above idle** (after a screen measurement) and **other**.

Nothing to measure separately: the baseline comes from the logs of the last 14 days. A **quiet minute** — every sample of the minute on battery, the discrete GPU asleep, no keyboard or mouse input for at least 60 s, at least 3 samples. The baseline is the median over the quietest 20 % of quiet minutes (total, CPU, rest and screen from the same minutes). It needs at least 20 quiet minutes; until then the tile shows how many are collected. Recomputed at start and every 30 minutes; per-minute results of finished logs are cached, only the current one is re-read.

---

## NVIDIA wake log

Every wake-up of the discrete GPU on battery costs several watts for 10–20 s (on the test laptop ~11 s awake at +8 W and ~6 s of falling asleep at +15 W); frequent wake-ups average up to ~8 W. The app logs each one: time, duration, energy above the usual level (median over the 30 s before) and the likely cause:
- **opened** — processes that got a context on the NVIDIA GPU at that moment (from “GPU Engine” counters; while it sleeps, the app remembers every 10 s who already has a context);
- **work** — processes that loaded its engines;
- coinciding events — brightness change, display connected, screen measurement;
- **unknown** — the GPU was queried without load (typically a monitoring app reading temperature or clocks).

Summary — the **NVIDIA: N×/h** button; the list — in its window; the log — `logs\gpu_wakes.csv`. While the GPU is awake on battery in the background, polling runs every 2 s so short wake-ups are not missed. The app itself does not wake the GPU (checked: DXGI, the WPF window, counters, brightness) — its own window is set to the integrated graphics.

**How the app avoids waking the GPU.** Any NVML query (and `nvidia-smi`) moves a sleeping GPU from D3 to D0 — about 8–9 W. So the app first reads the power state from Device Manager (does not wake it) and talks to NVML only when the GPU is already awake; in D3 it records 0 W. If the GPU sleeps at startup, NVML is initialized later, when it wakes on its own.

**A display keeps the GPU awake.** It cannot sleep while it outputs an image — usually an external monitor on a port wired to the discrete GPU. The Battery card shows it under *Displays*.

## Who keeps the GPU awake

An app may not load the discrete GPU yet keep video memory on it, and the GPU will not sleep (example: Figma left in the tray after closing its window held 197 MB and cost ≈ 8 W). The process sampler also reads each process’s video memory on the NVIDIA adapter (“GPU Process Memory” counters — they do not wake the GPU). If the GPU is awake with no load, its power is split between such holders by memory size (System, csrss and the app itself excluded). They are marked *holds* in Processes, named in the “GPU awake” notification and get advice: close the app completely or move it to the integrated graphics.

**Do not disable the discrete GPU in Device Manager to save power.** Without its driver nothing puts it to sleep, while Windows keeps reporting “D3”: on the test laptop a disabled GPU added +17 W. The app recognizes a disabled GPU (device status via `CM_Get_DevNode_Status`): the GPU row says *disabled — stays powered*, with a notification and advice; the log has `dgpu_disabled`.

---

## Screen measurement (OLED)

On an OLED panel there is no backlight: each pixel emits light, so the screen’s power depends on **what is shown**, not on brightness alone. A black pixel costs almost nothing.

**Measure screen** (Components card, on battery only, 2–3 minutes) shows full-screen **black**, then **white** at the current brightness and **white at 100 %**, then restores everything. Each phase takes 30 samples; the median of the calm ones is used. The result is how much a white image costs over a black one at each brightness (linear in between). The *Screen* row = that value at the current brightness × the **share of white** on screen: the average linear-light value of the subpixels in a 128×80 snapshot taken every 5 s while the window is open (never saved, ~5–10 ms of CPU). After a measurement *Rest* becomes *Other*.

A dark interface on OLED is nearly free: at 30 % brightness a fully white screen may cost ~1.5 W over black, and 2 % of “white” costs a few hundredths of a watt.

**The panel itself** (driver chips, refresh, link) is spent even on a black image. On laptops with classic S3 sleep the measurement adds a **screen off** phase (~20 s) and the difference *black vs off* is the panel’s own cost. On **Modern Standby** laptops switching the screen off puts the whole system to sleep (Windows logs “entering Modern Standby, reason SC_MONITORPOWER”), so this phase is skipped and the panel stays in Other/idle.

Interference handled during the measurement:
- **a brightness change wakes the NVIDIA GPU** (its driver watches the internal panel), and its falling asleep costs extra for several seconds — so a phase starts only after the GPU has slept for 10 s and 4 s after the image changed. If an external monitor keeps the GPU awake, it is polled instead and its power subtracted;
- **every CPU watt adds ~0.75 W to the rest** — samples where the CPU deviates from the phase median by more than 1.5 W are dropped;
- the app’s own NVIDIA polling and process sampling pause;
- if white differs from black by less than three standard errors, the result is not saved (*unreliable measurement*; the previous one is kept).

Other apps are not paused (that would need administrator rights and could hang them). The measurement stops if the charger is connected, the window is hidden, Esc is pressed or the test image is clicked. Brightness is restored on exit too, and — if the app crashed mid-measurement — on the next start.

---

## Notifications

On battery only, as Windows notifications from the tray icon; clicking opens the window. Each type at most once per 30 minutes; none during a screen measurement. Switch: **Battery notifications** in the tray menu.

| When | What it says |
|---|---|
| 10 minutes without keyboard or mouse input while the draw is above 1.5 × idle | the draw, the idle value and the likely culprit: the discrete GPU (with the cause from the wake log), otherwise the app with the highest draw, otherwise the CPU vs its idle |
| The discrete GPU has been awake for 5 minutes with no load above 30 % (a game is not a reason) | for how long, its power, the cause and the app holding its memory |
| An external monitor is on the discrete GPU | once per discharge |
| The discrete GPU is disabled in Device Manager | what to do; at most once per 30 minutes |
| Charge at 20 % and 10 % | time left at the current power; once each per discharge |

## Tray icon and background work

Closing the window hides the app in the tray; exit from the icon’s menu. Starting it again does not create a second copy — it opens the running one.

The icon shows colored digits — the current total power in watts (on AC the adapter estimate; *AC* only while there is no estimate). Color: blue — on battery, yellow — below 20 %, red — below 10 %, green — charging, gray — on AC; shades follow the taskbar theme. The tooltip: power, charge, time left. Menu: open, reset session, NVIDIA polling, notifications, **Start with Windows** (`HKCU\...\Run`, no admin), exit.

Polling interval:

| State | Interval |
|---|---|
| Window on screen | 1 s |
| In the background (battery, charging, AC) | 10 s |
| In the background on battery while the NVIDIA GPU is awake | 2 s |

A power source change and waking from sleep trigger a sample at once.

---

## Desktop PCs without a battery

The app detects at start that there is no battery and builds the window without the battery parts:
- one **CPU + graphics** tile (whole CPU package with integrated graphics + NVIDIA) with a wider sparkline;
- the chart without *Rest* and the charge background;
- the Components card full width, without the screen and rest rows;
- no Charge / Remaining / Health tiles, no Battery and Discharge session cards, no screen measurement, no before / after check, no On battery profile; the History tab explains it needs a battery;
- app data for Advice is collected all the time; advice about the laptop screen, GPU wake-ups, the battery power mode and an external monitor is not shown;
- Eco and Subzero apply all the time to the AC values of the power plan; apps are not moved to the integrated graphics.

**The whole PC’s draw is not shown**: a desktop has no sensor for it (the power supply does not report to Windows). Board, storage, fans and the monitor would need an external meter such as a smart plug. CPU power requires the Windows *Energy Meter* counters, which most laptops and many — not all — desktops expose.

Try this mode on a laptop with `BatteryCheckGui.exe --no-battery` (add `--no-log` to keep such samples out of the laptop’s logs).

## Updates and installer

**Installer** — `BatteryCheckSetup-<version>.exe` from [Releases](https://github.com/nik-agilefox/BatteryCheck/releases/latest). Per-user, no administrator rights: the app goes to `%LOCALAPPDATA%\Programs\BatteryCheck\bin`, logs to `…\BatteryCheck\logs`, a Start menu shortcut and an *Installed apps* entry are created. Options in the window: start with Windows, open when done.

Before installing it asks GitHub for the latest release (up to 6 s). If it is **newer than the version inside the installer**, that one is downloaded (the same package as the in-app update, size and SHA-256 verified) and installed instead. Offline, or if the download fails, the version inside the installer is installed. Switches: `/silent` (no windows, no autostart, no launch), `/offline` (skip the check).

The exe is not code-signed, so SmartScreen warns on first run: **More info → Run anyway**.

**In-app update** — the **Check updates** button in the footer; the app also checks at start and every 6 hours. When a newer release exists, the button turns into **Update to X** (release notes in its tooltip). It downloads `BatteryCheck-X.zip`, checks size and SHA-256, unpacks only plain files (no paths), swaps the program files — a running exe cannot be overwritten but can be renamed, so old files become `*.old` and are deleted on the next start; if any file fails, all are rolled back — and restarts. Logs and settings are untouched. A build running from the source folder is never updated from a release.

**Uninstall** from *Installed apps*: removes the program, the shortcut, autostart and the app’s own graphics preference; optionally also the logs and saved settings. If Eco or Subzero is on, switch to Standard first — that restores the Windows power settings the mode changed.

**Privacy.** Measurements stay in local CSV files. The only network requests are the update check and the download from GitHub.

---

## Command line

`BatteryCheckGui.exe`:

| Option | Effect |
|---|---|
| `--background` | start in the tray without a window (used by autostart) |
| `--exit` | close the running copy |
| `--no-gpu` | start with NVIDIA polling off |
| `--no-log` | do not write logs |
| `--no-battery` | behave like a desktop PC without a battery |
| `--theme light` / `--theme dark` | override the Windows theme |
| `--software-render` | draw without Direct3D (diagnostics: does a window state change wake the GPU) |

## Console version

`BatteryCheck.exe` — the same measurements in a console.

| Option | Effect |
|---|---|
| `-i`, `--interval <s>` | polling period, 1 s by default (the battery driver updates once a second) |
| `--no-gpu` | do not poll NVIDIA power (its sleep state is still tracked) |
| `--log <file>` / `--no-log` | log path (default `logs\battery_<date>.csv`) / no log |
| `--plain` | line output instead of a screen (automatic when output is redirected) |
| `-n`, `--count <N>` | take N samples and exit |
| `--lang en` / `--lang ua` | language for this run |
| `--replay <file or folder>` | replay recorded `battery_*.csv` logs instead of sensors |
| `--speed <N>` | with `--replay`: show the screen N times faster than real time |

Keys: **Q** or **Esc** — exit, **R** — reset the session.

**Replay** runs recorded logs through the same logic as live polling (smoothing, session, rest, idle baseline) and prints the result: time on battery, energy both ways, the discharges with indicator honesty. Without `--speed` the result is immediate (15 thousand samples in under a second). Useful for checking calculations on real data, including logs from other laptops.

---

## Data sources

| Source | Gives | Limits |
|---|---|---|
| Battery driver (`IOCTL_BATTERY_QUERY_*`) | design and full capacity, cycles, remaining capacity, voltage, charge/discharge power | power only on battery or while charging; the battery tag changes when the power source changes and is re-queried |
| Windows *Energy Meter* counters (RAPL) | CPU package, cores, integrated graphics | CPU only; on Intel Core Ultra the graphics tile may be counted only partly |
| Device Manager (`SPDRP_DEVICE_POWER_DATA`, `CM_Get_DevNode_Status`) | NVIDIA power state D0/D3, disabled device | — |
| NVML (`nvml.dll` from the NVIDIA driver) | GPU power, load, P-state | NVIDIA only; **wakes a sleeping GPU**, so used only when it is awake |
| Performance counters | per-process GPU engine load and video memory, “% C3 Time” | — |
| `NtQueryTimerResolution` | system timer resolution | which app holds it is not reported without admin |
| `GUID_CONSOLE_DISPLAY_STATE` notification | displays on / dimmed / off | on Modern Standby laptops “off” means the system is going to sleep |
| `EnumDisplayDevices` | connected displays and how many run through NVIDIA | recounted every 10 s |
| `GetLastInputInfo` | seconds without keyboard or mouse input | — |

On AC the total draw is not measured: the adapter has no sensor, and the ACPI Power Meter counter is not supported on typical laptops.

## Log files

All in the `logs` folder next to `bin`: CSV, comma-separated, decimal point, UTF-8; flushed to disk once a minute so the disk is not woken every second.

**`battery_<date>_<time>.csv`** — one row per sample:

| Column | Meaning |
|---|---|
| `time` | local time, ISO 8601 |
| `power_state`, `on_ac`, `charging`, `discharging` | power state flags |
| `capacity_mwh`, `full_mwh`, `soc_pct` | remaining and full capacity, charge % |
| `voltage_mv`, `rate_mw`, `current_a` | voltage, power (< 0 — discharge), current |
| `cpu_pkg_w`, `cpu_cores_w`, `igpu_w`, `dram_w` | CPU power by domain, W |
| `dgpu_w`, `dgpu_util_pct`, `dgpu_pstate`, `dgpu_dstate` | NVIDIA power, load, P-state, D-state (0 — awake, 3 — asleep) |
| `displays`, `displays_on_dgpu` | connected displays, of them through NVIDIA |
| `rest_w` | rest = battery − CPU − GPUs, W (on battery) |
| `idle_s` | seconds without keyboard or mouse input |
| `brightness_pct`, `screen_w` | brightness and screen image estimate (window open, screen measured) |
| `dgpu_disabled` | 1 — NVIDIA disabled in Device Manager |
| `cpu_c3_pct`, `timer_ms` | cores in deepest sleep, system timer resolution |
| `display_state` | 0 — displays off, 1 — on, 2 — dimmed |

| File | Contents |
|---|---|
| `process_energy_YYYYMMDD.csv` | per minute: `minute,process,wh,fg_wh,gpu_wh,seconds`; the `_battery` row is the total energy and seconds of that minute |
| `process_paths.csv` | process name → exe path (for History tooltips) |
| `gpu_wakes.csv` | `start,end,seconds,cost_wh,flags,cause` |
| `actions.csv` | every change the app made to the system: `time,action,from,to,by` (profile or you) |
| `experiments.csv` | before / after check results |

## Settings

Stored in `HKCU\Software\BatteryCheck`: language, chart mode and period, charger rating, last measured rest, screen measurement, power mode and the values it saved for restoring, profile settings and state, notification and process-analysis switches. Uninstalling keeps them unless you choose to delete your data.

---

## Source layout

```
src/Core/            measurements, logs and logic shared by the window and console versions
  AppInfo.cs           version, repository, release parsing
  Battery.cs           battery driver access
  Sensors.cs           RAPL, NVIDIA state and power, displays, display state, timer, deep sleep
  Sampler.cs           polling loop and snapshot
  Stats.cs             sample, smoothing, discharge session, CSV log
  Processes.cs         per-process CPU cycles, GPU load and video memory
  ProcessEnergy.cs     active window, per-minute app energy log
  CycleProcesses.cs    apps per discharge for History, exe paths
  DischargeHistory.cs  discharges from logs, charge indicator honesty
  Baseline.cs          idle baseline from quiet minutes, extra watts
  Experiment.cs        per-minute recorder, before / after comparison
  GpuWakes.cs          NVIDIA wake log and culprits
  Profile.cs           On battery profile logic, action log
  Efficiency.cs        efficiency mode (EcoQoS + priority)
  BackgroundEfficiency.cs  efficiency mode for all background apps (Eco)
  MaxEconomy.cs        power plan values for Eco / Subzero
  GpuPreference.cs     per-app graphics preference (integrated GPU)
  Rules.cs, rules.json advice rules, vendors, user rules
  ProcessNorm.cs, ProcessEnergyStats.cs, QuietProcesses.cs   per-app norms and quiet minutes
  Notifier.cs          notification rules
  Replay.cs            replaying recorded logs instead of sensors
  Localization.cs      L.T("English", "Українська"), plurals, number culture
src/Gui/             the window (WPF without XAML), tray, updater
src/Console/         console version
src/Setup/           installer and uninstaller
tests/               core tests (test.cmd)
tools/               release.ps1, screenshot.ps1, wake probe, icon generator
```

## Tests

`test.cmd` builds `bin\tests.exe` from the core and `tests\Tests.cs` and runs it; the exit code is the number of failed tests. The tests use synthetic logs with known answers: session energy at 1 and 10 s steps, sleep gaps, rest, old log formats, charge indicator honesty (honest / 25 % faster / jump / drift / too little data), the wake log migration, idle baseline, before / after, the profile and power mode logic with fake hosts, efficiency mode on the test’s own process, rules, release parsing and version comparison, plurals and units in both languages.
