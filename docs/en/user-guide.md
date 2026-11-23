# Player guide

[简体中文](../user-guide.md) · **English** · [日本語](../ja/user-guide.md) · [한국어](../ko/user-guide.md)

## First launch

1. Put `FH6Telemetry.exe` in a writable folder and open it. Use the button at the top of the app to show or hide the HUD. [Download the Windows x64 release](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest/download/FH6Telemetry.exe)
2. Enable Data Out in FH6 under **Settings → HUD and Gameplay**. On the same PC, use IP `127.0.0.1` and port `20440`. Enter the same port in the app's **Settings** tab. [Forza Data Out documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)
3. Click **Start Listening**, return to free roam, and check the **Overview** tab for live speed, gear, and RPM. While stopped, lightly press the brake to confirm that telemetry is still arriving.

Use **HUD Settings** to change placement, scale, background opacity, text outlines, and visible metrics. Click **Save HUD Settings** to keep your choices.

## Automatic shifting

Select **Manual** or **Manual with Clutch** in FH6, then pick an output mode in **Auto-Shift**.

| Output mode | Use it when | Default input bindings |
| --- | --- | --- |
| Physical Pad Compatible · Keyboard Shifting | Your physical controller already works in FH6. The app sends only keyboard shift inputs. | `E` up, `Q` down, left `Shift` clutch, `W` throttle blip. Match the active keyboard layout. |
| Single-Controller Relay · Built-in Clutch & Blip | You want the app to forward an XInput controller through a virtual Xbox pad. Use this mode for active drive assist. | `B` up, `X` down, `L3` clutch, `RT` throttle, `LT` brake, and left stick for steering. |

For the relay, follow **Virtual Pad** to install ViGEmBus and HidHide. In HidHide, allow the runtime application path shown on that page and select your physical controller. Keep the relay running when you open FH6 so the game can detect the virtual pad. [Complete relay setup](agent-deployment.md#single-controller-relay)

With Auto-Shift and Drive Assist off, park in first gear and select **Virtual Pad → Check stationary upshift**. Return to the game within 15 seconds, hold the brake, and release the other pedals and handbrake. A change to second gear in the game's telemetry confirms the check. After that, use **Auto-Shift** or its default `F9` shortcut to start normal driving. Disabling Auto-Shift releases automatic shift inputs. Use the relay disconnect control when you want to stop forwarding the controller.

Automatic clutch starts enabled. Race-transmission detection uses the result of a suitable shift to learn whether clutch input is required. If the app reports an unconfirmed shift, try the same keys by hand and check the selected control preset, gearbox mode, and game focus.

## Drive assist and tight turns

1. Set up the single-controller relay. Select a feature or apply a preset in **Drive Assist**.
2. For active output, choose **Confirm offline practice**, then **Arm drive assist**. Shadow Mode can show proposed behavior, and Fair Play is read-only.
3. When using the game's ABS or traction control, select the matching options in the app. The default `Pause` key stops assist output. Review or change the binding in **Shortcuts**.

Under **Experimental → Drive Assist**, enable **Preserve Steering in Slow, Tight Turns**. When the car is stable and the outside front tire still grips, sustained driver steering takes priority. The regular protections can intervene when the tire loses grip or the rear breaks loose.

**Manual Speed Limit** sets the highest activation speed, from 20 to 90 km/h, with a 54 km/h default. **Learn Speed Limit from Cornering** starts with that value and updates it between turns after enough stable samples. Learning restarts after a car or performance-index change or an app restart. The chosen mode and manual value are saved. The HUD shows the learned speed by default. Toggle **HUD Settings → Tight-turn limit reminder** and save to control the readout.

## Records, replay, and updates

Start a drive recording in **Diagnostics** and save it after the event. Under **Drive Assist → Offline replay and learned route**, compare physical input, recorded output, and the output calculated with current settings. Replay uses the recorded vehicle motion, so confirm handling changes with a live drive.

The default settings directory is `%APPDATA%\FH6Telemetry`. A portable `Data\telemetry-settings.json` beside the launcher can be used when the standard settings file does not exist. **History** opens the active records folder. Back up the active settings and records before an upgrade.

**About** opens the project, the guide in your app language, and the latest release. **Check for updates** queries GitHub. If automatic download is enabled, the new EXE is saved in the updates folder for you to launch. [Release downloads](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest)

## Troubleshooting

| Symptom | Check first |
| --- | --- |
| Waiting for telemetry | Data Out is enabled, address and port match, FH6 is in a driving screen, and one program owns the UDP port. |
| Packets arrive but readings stay blank | Check packet length and parse errors in **Diagnostics**. |
| Duplicate inputs or no controller | Check the runtime path in **Virtual Pad**, the selected HidHide device, and whether the game sees the virtual pad. |
| A shift is sent but the gear does not change | Test the same binding manually. Check the clutch mode, current gear, and foreground window. |
| Tight-turn speed stays at the manual value | Arm Drive Assist, enable the experiment and automatic learning, and collect stable turns. |
| Restore the physical controller | Disconnect the relay or exit normally. If you set HidHide manually, restore that device's prior visibility. |

[Source builds and complete deployment checks](agent-deployment.md)
