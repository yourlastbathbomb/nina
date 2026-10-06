# M2 partial: mount read-out and no-slew tests, 2026-10-06

These are short indoor checks on the real mount over the FTDI cable (`/dev/cu.usbserial-DU0D8VUG`), done with `lx200probe raw`. The scope was not slewed, aligned or initialised. Raw byte traces are in `~/Astro/NINA/m2-bench/20261006_170805_readonly/` (`trace.log`, `focus_guide/`, `focus_guide2/`, `guide3/`).

## Read-only answers

| Query | Reply | Meaning |
|---|---|---|
| ACK (0x06) | `A` | Alt-az mode |
| `:GVP#` | `LX2001#` | LX200GPS / Autostar II |
| `:GVN#` | `4.0g#` | **Firmware 4.0g**, older than the 4.2g the plan assumed |
| `:GVD#` / `:GVT#` | `Oct 06 2005#` / `10:33:54#` | Firmware build date and time |
| `:GW#` | no reply (2 s timeout) | **Not supported on 4.0g.** The driver never writes the site when alignment is unknown |
| `:GR#` / `:GD#` | `00:00:00#` / `+00\xDF00:00#` | High precision (long format) is already on; the degree byte is `0xDF` |
| `:GA#` / `:GZ#` | `+00\xDF00:00#` / `000\xDF00:00#` | Not initialised: alt/az are not reported before alignment (pulses below did not change them) |
| `:GS#` / `:GL#` / `:GC#` | `00:02:20#` / `08:02:20#` / `01/01/01#` | Clock and date never set (indoors, no GPS fix) |
| `:Gt#` / `:Gg#` / `:GG#` | `+22\xDF15:00#` / `245\xDF49:19#` / `-08#` | Site already correct: 22.25 N, 114.18 E (west-positive), UTC+8 |
| `:GT#` | `+60.0#` | Sidereal tracking rate |
| `:Go#` | `-90\xDF#` | Lower slew limit |
| `:D#` | `#` | Not slewing |

## No-slew motion tests

| Test | Commands | Result |
|---|---|---|
| Focuser, host-timed | `:F2#`, `:F-#`, 1-1.5 s, `:FQ#` | Focus motor heard. The drawtube movement was too small to see |
| Focuser, mount-timed pulse | `:FP+1000#`, `:FP-1000#`, `:FP+1500#` | Accepted (no reply, as documented). Not yet separated by ear from the host-timed move, so this needs re-checking at the bench |
| **Pulse guiding in alt-az** | `:Mgn3000#`, `:Mgs3000#`, `:Mge3000#`, `:Mgw3000#`, 4 s apart | **The mount motors ran for all four.** 4.0g accepts `:Mg` in alt-az, even unaligned. This clears plan risk #1 for acceptance; direction and size per axis still need measuring after an alignment |

Every run ended with the probe's stop set: `:Q#`, `:Qn/s/e/w#` and `:FQ#` twice.

## Still open for the bench session

- **Firmware flash 4.0g → StarPatch 4.2G**, planned, on a Windows laptop with StarPatch at 9600 baud, about an hour. Redo this read-out after flashing.
- Then the full `lx200probe checklist`: date convention, goto/sync, pulse-guide magnitude per axis, `:FP` vs host-timed focus with backlash, and `:AL#`/`:AA#` alignment retention.
- Probe usability: `:GVF#`, `:GM#` and `:Gc#` are not in the catalogue, so they prompt like motion commands. Speed commands (`:F1#`-`:F4#`) don't prompt. Scripted runs must not queue a "y" after them.
