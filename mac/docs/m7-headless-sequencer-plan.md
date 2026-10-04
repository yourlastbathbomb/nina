# M7: headless advanced sequencer on macOS (design)

*Read-only scout, 2026-10-04. Repo `nina` on branch `macos` (upstream `isbeorn/nina` develop @ `ee69f27` plus fork commits `ba53e8547`, `0cb62e5bf`). All `file:line` references point at upstream sources in this repo unless they start with `mac/`. Searches used `/usr/bin/grep`. The compile probes ran in throwaway projects under `/private/tmp/m7probe`. Nothing in the repo was built or changed, apart from creating this file.*

Scope: milestone **M7** of `../../../MAC_PORT_PLAN.md` §4. That milestone covers a static item catalogue in place of the plugin loader's WPF parts, Target form → sequence tree, a MaxAltitude condition and a field-rotation symbol. It is done when a simulated night writes correctly named files and stops at the horizon, at max altitude and at dawn.

---

## 0. Summary

- **The engine compiles headless.** A compile probe built 188 of NINA.Sequencer's 261 non-XAML `.cs` files unchanged for plain `net10.0`, with no WPF and no Windows targeting. These are the files the rig's sequences need. The probe added two trimmed copies of upstream `Editing/` files and a few dozen inert WPF stand-ins (§1.6). Only 5 errors were left, at 5 call sites (`MyMessageBox.Show`, `IWindowService.Show`). Each comes from the probe referencing a *Windows-built* `NINA.Core.dll` whose signatures use PresentationFramework enums. They go away once `NINA.Core.Mac` and the sequencer compile against the same compat enums (§1.6, Appendix A).
- **Some files must stay out:** `View/**` and `Behaviors/**` (25 `.cs`), all 37 `*.xaml.cs` and 40 `.xaml`, `Logic/ExprConverter.cs`, `Properties/AssemblyInfo.cs` (`ThemeInfo`), and four WPF-binding files in `Editing/`. Two of those four come back as headless copies. Removing the 41 droppable item files listed in §1.5 is a product decision rather than a compile necessity. With every item kept, the probe compiled everything except `TakeSubframeExposure.cs`, which uses `System.Windows.Forms`.
- **NINA.CustomControlLibrary is not needed.** 0 `.cs` files in NINA.Sequencer reference it. Its 26 uses are all XAML.
- **NINA.WPF.Base contributes a small slice:**
  - 42 upstream files compile for `net10.0` against the compat stand-ins: 7 interfaces, `PlateSolvingStatusVM`, `DockableVM`, `BaseVM`, `ImageHistoryPoint`, the 8 MvvmLight files, the 7 AutoFocus-report files and the 16 mediators.
  - The full `IFramingAssistantVM` drags in `SkySurvey` and GDI+. It is replaced by a 1-member interface, because `DeepSkyObjectContainer.cs:227` calls only `SetCoordinates`.
  - `CameraVM`, `TelescopeVM`, `GuiderVM` and `FocuserVM`, plus 5 helper files, also compiled in the probe. The device "service layer" can therefore reuse upstream code.
- **Static catalogue:** one hand-written list of types, built by direct constructor calls, so upstream constructor changes break the build loudly.
  - Metadata comes from the types' own `[ExportMetadata]` through upstream's `SequenceEntityExtension.AddMetaData` (`Utility/SequenceEntityExtensions.cs:19-51`). That helper exists for exactly this purpose.
  - The factory is a `HeadlessSequencerFactory : ISequencerFactory` returning `null` views. Upstream's own test `CorpusSequencerFactory` already does this (`NINA.Test/Sequencer/Serialization/LegacySequenceMigrationCorpusTest.cs:1206-1265`).
  - No MEF and no ResourceDictionaries are needed.
- **Existing NINA `.json` sequences and templates load unchanged** if the mac assembly is named **`NINA.Sequencer`**. `$type` strings are assembly-qualified (`SequenceJsonConverter.cs:43-62`, `JsonCreationConverter.cs:212-224`). Entities outside the catalogue become `Unknown*` placeholders, which are skipped at run time and flagged by validation. The bundled templates and upstream's 200-entity v3.2 corpus are ready-made fixtures.
- **New code in one fork assembly, `NINA.Mac.Sequencing`:**
  - `MaxAltitudeCondition`: a mirror of `AltitudeCondition` with the comparison inverted and no "rising" exemption. A prototype compiled through the upstream source generator.
  - `FieldRotationCalculator`: a C# prototype that reproduces all 6 values of the plan §6 table.
  - A `FieldRotation` symbol provider registered through the public `ISymbolBroker.RegisterSymbolProvider`.
  - `NightPlan` → tree generator, modelled on upstream `SimpleDSOContainer.TransformToDSOContainer` (`NINA/ViewModel/Sequencer/SimpleSequence/SimpleDSOContainer.cs:758-795`).
  - The headless composition root.
- **Correction to earlier research:** symbol qualification uses `_`, not `.`. The fallback max-altitude predicate is `Mount_Altitude <= 75`, not `Mount.Altitude` (`Logic/SymbolBroker.cs:119`, `:452-470`). This comes from reading the code; it was not run.
- **Blockers outside M7:**
  - `NINA.Equipment.Mac`, `NINA.Image.Mac` and `NINA.PlateSolving.Mac` don't exist yet.
  - `NINA.Mac.WpfCompat` must provide the surface listed in §1.4. Some of it must work at run time, not just compile (`WeakEventManager`, `Dispatcher`, `Application.Current`).
  - `CenterAfterDriftTrigger` hard-codes `new PlateSolverFactoryProxy()` and `new WindowServiceFactory()` (`Trigger/Platesolving/CenterAfterDriftTrigger.cs:141-142`). Simulated drift-centring therefore needs M6's solver, a fake ASTAP executable, or upstream patch P2 (§6.7).
- **Effort:** 12–17.5 working days, about 2.5–3.5 weeks, against the plan's 1.5–2.5 weeks. The difference is the headless device/imaging host and the simulated-night harness. The plan put those implicitly in M8.

---

## 1. What NINA.Sequencer uses from NINA.WPF.Base, NINA.CustomControlLibrary and WPF

### 1.1 Inventory

| Set | Count | How measured |
|---|---|---|
| All `.cs` under `NINA.Sequencer` | 298 | `/usr/bin/find NINA.Sequencer -name '*.cs' \| wc -l` |
| …of which `*.xaml.cs` | 37 | same, `-name '*.xaml.cs'` |
| Non-XAML `.cs` | 261 | 298 − 37 |
| `.xaml` | 40 | |
| Non-XAML `.cs` in `View/` (converters, selectors) + `Behaviors/` | 16 + 9 | directory listing |
| Compiled by upstream (`NINA.Sequencer.csproj:24-30` removes `Converters/**`, `Logic/ValConverter.cs`) | 260 | |
| Headless candidate set ("all" probe) | 234 | 261 − 25 (View/Behaviors) − `ValConverter` − `AssemblyInfo` |
| **Rig set compiled by the probe** | **188 upstream + 2 trimmed copies** | `msbuild -getItem:Compile` on the probe (Appendix A) |
| Exported entities (`[Export(typeof(…))]`) | 104 types | script over `[Export]`/`[ImportingConstructor]` |
| `[UsesExpressions]` entities | 37 in the headless set, 24 in the rig set | `/usr/bin/grep -l "\[UsesExpressions"` |

### 1.2 NINA.CustomControlLibrary

- `.cs` files referencing it: **0**.
- `.xaml` files: **26 of 40**, all through `clr-namespace:NINA.CustomControlLibrary;assembly=NINA.CustomControlLibrary`.
- The only other mention is the `ProjectReference` at `NINA.Sequencer.csproj:54`.

The headless project drops the reference.

### 1.3 NINA.WPF.Base, per file

These are the type references in non-XAML files, from a word-boundary scan of every public type that WPF.Base declares in the 7 namespaces the sequencer imports. Separately, 10 files use `GalaSoft.MvvmLight.Command.RelayCommand`. That class lives in `NINA.WPF.Base/Utility/MVVMLight/RelayCommand.cs` and calls `CommandManager` at `:139,159,197`.

**Kept (rig set):**

| File | WPF.Base types used |
|---|---|
| `Container/DeepSkyObjectContainer.cs` | `IFramingAssistantVM` (ctor; `SetCoordinates` at `:227`), `IApplicationMediator` (`ChangeTab` at `:226`), RelayCommand ×4 |
| `Logic/SymbolBroker.cs` | `DockableVM` (base class, `:55`) |
| `SequenceItem/Imaging/TakeExposure.cs`, `TakeManyExposures.cs`, `SmartExposure.cs` | `IImageHistoryVM`, `IImageSaveMediator` |
| `SequenceItem/FlatDevice/AutoExposureFlat.cs` (kept compiled, not catalogued) | `IImageHistoryVM`, `IImageSaveMediator` |
| `SequenceItem/Platesolving/Center.cs`, `SolveAndSync.cs` | `PlateSolvingStatusVM` |
| `Trigger/Guider/DitherAfterExposures.cs` | `IImageHistoryVM`: the dither count *is* `history.ImageHistory.Count` (`:82-111`) |
| `Trigger/Platesolving/CenterAfterDriftTrigger.cs` | `IApplicationStatusMediator`, `IImageSaveMediator`, `PlateSolvingStatusVM` |
| `Trigger/Platesolving/PlatesolvingImageFollower.cs` | `IApplicationStatusMediator`, `IImageSaveMediator`, `BeforeImageSavedEventArgs` |
| `SequenceItem/SequenceItem.cs`, `Trigger/SequenceTrigger.cs`, `Conditions/SequenceCondition.cs`, `Container/SequenceContainer.cs`, `SequenceRootContainer.cs`, `LinkedTemplateContainer.cs`, `SequenceItem/Utility/ExternalScript.cs` | `GalaSoft…RelayCommand` |

**Dropped files and the WPF.Base types they would have pulled in:**

| Files | WPF.Base types |
|---|---|
| `RunAutofocus` + 5 autofocus triggers | `IAutoFocusVMFactory` → AutoFocusVM stack |
| `MeridianFlipTrigger`, `ProgrammableMeridianFlipTrigger` | `IMeridianFlipVMFactory`, `IApplicationStatusMediator` |
| `SkyFlat`, `Trained*`, `AutoBrightnessFlat`, `TakeSubframeExposure` | `IImageHistoryVM`/`IImageSaveMediator` |
| `SolveAndRotate` | `PlateSolvingStatusVM` |
| `MoveRotatorMechanical` | the concrete `RotatorMediator` |
| `LoadImagingLayout` | `IApplicationMediator` |
| `SynchronizeDomeTrigger` | `IApplicationStatusMediator` |
| `View/MiniSequencer/MiniSequencerDataTemplateSelector.cs` | `DataTemplatePostfix` |

**The WPF.Base slice the rig set needs.** It compiled for `net10.0` against the compat stand-ins; only type-identity artefacts remained (Appendix A).

| Group | Files |
|---|---|
| `Interfaces/ViewModel/` | `IImageHistoryVM`, `IApplicationStatusVM`, `IApplicationVM`, `IImageSaveController` (+ `IImageStatisticsVM` if `ImagingVM` is linked, §4) |
| `Interfaces/Mediator/` | `IApplicationMediator`, `IApplicationStatusMediator`, `IImageSaveMediator` |
| `ViewModel/` | `PlateSolvingStatusVM`, `DockableVM`, `BaseVM` |
| `Model/` | `ImageHistoryPoint` |
| `Utility/MVVMLight/*.cs` | 8 files |
| `Utility/AutoFocus/*.cs` | 7 files; needed because `IImageHistoryVM.AppendAutoFocusPoint(AutoFocusReport)` pulls in the fitting classes and Accord |
| `Mediator/*.cs` | 16 thin mediators |
| Optional, M7b/M8 | `ViewModel/Equipment/Camera/CameraVM.cs`, `Telescope/{TelescopeVM,TelescopeSlewCoordinates,TelescopeLatLongSyncVM}.cs`, `Guider/GuiderVM.cs`, `Focuser/{FocuserVM,FocuserDecorator,AbsoluteBacklashCompensationDecorator,OvershootBacklashCompensationDecorator}.cs`. These compiled too. |
| Replaced | `Interfaces/ViewModel/IFramingAssistantVM.cs`, reduced to `Task<bool> SetCoordinates(DeepSkyObject dso);` |

Packages the slice needed in the probe:
- CommunityToolkit.Mvvm 8.4.2
- CsvHelper 33.1.0
- Accord, Accord.Math and Accord.Statistics 3.8.2-alpha
- AsyncEnumerator 4.0.2
- Newtonsoft.Json 13.0.4
- Nito.AsyncEx 5.1.2
- OxyPlot.Core 2.2.0
- System.ComponentModel.Composition 10.0.10

Removing any one of them added errors. Several only arrive transitively in the real build.

### 1.4 WPF itself, per type

`System.Windows.Input.ICommand` lives in `System.ObjectModel`, part of the base runtime. Files that use only `ICommand` therefore compile without compat, and the probe confirmed it.

The table covers what the rig set (188 files plus the two trimmed copies) needs from WPF. "Run-time" means the stand-in is executed on the headless path and must behave correctly. "Compile" means it only has to exist.

| WPF type / members | Used at | Need |
|---|---|---|
| `System.Windows.Media.GeometryGroup` | `Interfaces/ISequenceEntity.cs:31`, `SequenceItem.cs:63`, `SequenceTrigger.cs:74`, `SequenceCondition.cs:59`, `LinkedTemplateContainer.cs:223-226`, `Utility/SequenceEntityExtensions.cs:24,32,40,48` | compile (Icon stays `null` headless) |
| `SolidColorBrush(Color)`, `Color`, `Colors.White/Orange/Red` | `Logic/Expression.cs:279-285` (`InfoButtonColor` getter) | compile |
| `WeakEventManager<TSource,TArgs>.AddHandler/RemoveHandler` | `DeepSkyObjectContainer.cs:97-99,155,159`, `LinkedTemplateContainer.cs:524,530`, `SwitchFilter.cs:71`, `SmartExposure.cs:92`, `SlewScopeToAltAz.cs:55` | **run-time**: it propagates DSO target coordinates to children and profile location/horizon changes |
| `Application.Current`, `.Dispatcher`, `.Resources[...]`, `.Resources.Contains` | `LinkedTemplateContainer.cs:224`, `SymbolController.cs:86`, `SymbolFunctionController.cs:83`, `TargetController.cs:193,231`, `TemplateController.cs:186,211` | **run-time if the controllers are used**: `TemplateController.cs:186,211` and `TargetController.cs:193,231` dereference `Application.Current` without `?.` |
| `Threading.Dispatcher` (`CurrentDispatcher`, `CheckAccess`, `VerifyAccess`, `InvokeAsync(Action)`→`DispatcherOperation.Task`, `InvokeAsync(Action,DispatcherPriority,CancellationToken)`, `BeginInvoke(DispatcherPriority,Delegate)`), `DispatcherPriority` (incl. `DataBind`), `DispatcherSynchronizationContext` | `Editing/SequenceEditHistory.cs:31,96,113,158`, `LinkedTemplateContainer.Editing.cs:36,74-75,155`, controllers | run-time (`SequenceEditHistory` field initialiser), otherwise compile |
| `Input.CommandManager.InvalidateRequerySuggested`, `RequerySuggested` | `LinkedTemplateContainer.cs:113,126`, `LinkedTemplateContainer.Editing.cs:90`, MvvmLight `RelayCommand.cs:139,159,197` | run-time no-op |
| `Input.ModifierKeys` | `Interfaces/DragDrop/IDroppable.cs:47`, `TemplateController.cs:398` | compile |
| `ComponentModel.ICollectionView` (`GroupDescriptions`, `SortDescriptions`, `Filter`, `Refresh`), `GroupDescription`, `SortDescription`; `Data.CollectionViewSource` (`GetDefaultView`, `Source`, `View`, `GroupDescriptions`, `SortDescriptions`, `Filter`/`FilterEventArgs`/`FilterEventHandler`), `PropertyGroupDescription` | `Interfaces/ISequencerFactory.cs:31-34`, `SequencerFactory.cs:81-105`, `SymbolController.cs:27-29,43-44`, `SymbolFunctionController.cs:24-26,40-41`, `TargetController.cs:49-52,94`, `TemplateController.cs:58-61` | compile (runtime only if upstream `SequencerFactory` or the controllers are instantiated; §2.2 avoids that) |
| `MessageBoxButton`, `MessageBoxResult` | `Sequencer.cs:115-121`, `SequenceRootContainer.cs:58,66`, through `NINA.Core.MyMessageBox` | compile; must be the **same type** `NINA.Core.Mac` uses |
| `ResizeMode`, `WindowStyle` | `Center.cs:169`, `SolveAndSync.cs:98`, through `NINA.Core.Utility.WindowService.IWindowService.Show` | compile; same-type rule |
| `Microsoft.Win32.OpenFileDialog` (`Title`, `FileName`, `DefaultExt`, `Filter`, `ShowDialog`) | `ExternalScript.cs:47-53` (UI command) | compile |
| `Controls.TextBox` (`TextProperty`, `GetBindingExpression`, `ToolTip`), `Data.BindingExpression.ResolvedSource`, minimal `DependencyProperty`/`FrameworkElement` | `Logic/UserSymbol.cs:289-352` (`ShowSymbols` UI handler) | compile |
| Namespaces only: `System.Windows.Documents`, `.Controls`, `.Data` | `SymbolBroker.cs:49`, `LoopWhile.cs:12`, `UserSymbol.cs:26-27`, `SequenceContainer.cs:36` | compile (a namespace with any type) |
| `Media.Imaging.BitmapSource` | indirectly: `IRenderedImage.Image`, `ImageSavedEventArgs.Image`, `PlateSolvingStatusVM.Thumbnail` | engine-wide compat type; same-type rule |

**Not needed, because the WPF-binding Editing files are replaced:**
- `DependencyObject`/`DependencyProperty` registration and attached properties
- `FrameworkPropertyMetadata`
- `Binding`, `BindingExpressionBase`, `MultiBindingExpression`, `PriorityBindingExpression`, `BindingOperations.GetBindingExpressionBase`
- `DependencyPropertyDescriptor`, `LocalValueEnumerator`
- `Selector`, `TextBoxBase`, `ComboBox`, `DatePicker`, `DispatcherTimer`
- `VisualTreeHelper`, `LogicalTreeHelper`

The probe showed this surface keeps growing (§1.6, round 6). Shimming it is the wrong trade.

`NINA.Core` types that the sequencer needs `NINA.Core.Mac` to keep, with these signatures:
- `MyMessageBox.Show(string,string,MessageBoxButton,MessageBoxResult)`
- `IWindowServiceFactory`, `IWindowService` and the concrete `WindowServiceFactory`, which `CenterAfterDriftTrigger.cs:142` constructs directly
- `Notification.ShowError/ShowWarning/ShowInformation`: 15 sequencer files call it
- `AsyncObservableCollection`, `IApplicationResourceDictionary`, `CoreUtil.Delay`, `Loc`, `Logger`, `BaseINPC`

**Headless behaviour needed:**
- `MyMessageBox` returns a configured default.
- `WindowService.Show` and `DelayedClose` do nothing. Upstream's version dereferences `Application.Current.MainWindow` (`WindowService.cs:68`).
- `Notification` routes to the log and to an event the app can show.

### 1.5 Which files the rig needs

**Needed and catalogued** (exposed to the generator, loader and UI):

| Area | Entities |
|---|---|
| Containers | `SequenceRootContainer`, `StartAreaContainer`, `TargetAreaContainer`, `EndAreaContainer`, `SequentialContainer` (the "instruction set"), `ParallelContainer`, `DeepSkyObjectContainer`, `ConditionalContainer` |
| Telescope / solving | `SlewScopeToRaDec`, `SlewScopeToAltAz` (soft-park position), `ParkScope` (M4 driver soft-parks), `UnparkScope`, `SetTracking`, `Center`, `SolveAndSync` |
| Imaging | `TakeExposure`, `TakeManyExposures`, `SmartExposure` |
| Camera | `CoolCamera`, `WarmCamera`, `DewHeater` (ASI anti-dew), `SetUSBLimit` |
| Guider | `Dither` (direct, through `DirectGuider.cs:228-275`) |
| Utility | `WaitForTime`, `WaitForTimeSpan`, `WaitForAltitude`, `WaitUntilAboveHorizon`, `WaitForSunAltitude`, `WaitForMoonAltitude`, `WaitUntil` (expression), `Annotation`, `ExternalScript` (siril-cli) |
| Expressions | `Constant`, `Variable`, `GlobalConstant`, `GlobalVariable`, `ResetVariable`, `ResetVariableToDate` |
| Conditions | `LoopCondition`, `TimeCondition` (dusk/dawn providers), `TimeSpanCondition`, `AltitudeCondition` (min altitude), `AboveHorizonCondition`, `SunAltitudeCondition`, `MoonAltitudeCondition`, `MoonIlluminationCondition`, `LoopWhile`, **`MaxAltitudeCondition` (new, §5.5)** |
| Triggers | `DitherAfterExposures`, `CenterAfterDriftTrigger` (+ helper `PlatesolvingImageFollower`), `ReconnectTrigger`, `ReconnectOnDownloadFailure` |

**Compiled but not catalogued in M7:**
- `SwitchFilter`: `SmartExposure`'s constructor news it up (`SmartExposure.cs:59-74`). It is a no-op while the wheel reports disconnected (`SwitchFilter.cs:179-189`).
- `AutoExposureFlat` and the four items it composes (`CloseCover`, `OpenCover`, `SetBrightness`, `ToggleLight`): reserved for the M9 calibration screen.
- `ConnectAllEquipment`, `ConnectEquipment`, `DisconnectAllEquipment`, `DisconnectEquipment`: catalogue them once M8's connection service exists.
- `SetReadoutMode`.
- `LinkedTemplateContainer`, plus `TemplateController`/`TemplateLinkResolver` for its `TemplatedSequenceContainer`: these must compile because `Serialization/SequenceJsonConverter.cs:75-80` and `SequenceContainerCreationConverter.cs:32,57` reference the type.
- Infrastructure: `Sequencer`, `SequencerFactory` (unused), the `Serialization/*` converters, `Logic/*`, `Editing/*` (journal compiled but never activated), and `Utility/*` including the 11 date-time providers.

**Droppable for this alt-az rig:** 41 files, excluded from the compile.

| Area | Files |
|---|---|
| Autofocus | `SequenceItem/Autofocus/RunAutofocus.cs`, `Trigger/Autofocus/*` (5) |
| **Meridian flip** | `Trigger/MeridianFlip/MeridianFlipTrigger.cs`, `ProgrammableMeridianFlipTrigger.cs`. These **must** go. `ItemUtility.GetMeridianFlipTime` (`Utility/ItemUtility.cs:95-111`) then returns `DateTime.MinValue`, so `IsTooCloseToMeridianFlip` is always false and never delays a dither or centre. Keep `Interfaces/IMeridianFlipTrigger.cs`, which `ItemUtility.cs:101` references. |
| Rotator | `MoveRotatorMechanical`, `Platesolving/CenterAndRotate`, `SolveAndRotate` |
| Flat panel / twilight | `AutoBrightnessFlat`, `SkyFlat`, `TrainedFlatExposure`, `TrainedDarkFlatExposure` |
| Dome | `SequenceItem/Dome/*` (8), `Trigger/Dome/SynchronizeDomeTrigger` |
| Switch, safety | `SetSwitchValue`, `WaitUntilSafe`, `SafetyMonitorCondition`, `TriggerOnUnsafe` |
| Guider beyond direct dither | `StartGuiding`, `StopGuiding`, `RestoreGuiding` |
| Focuser items | `MoveFocuserAbsolute`, `MoveFocuserRelative`, `MoveFocuserByTemperature`. Focusing is manual and the timed focuser has no position readout. |
| UI-only / Windows-only | `TakeSubframeExposure` (WinForms), `LoadImagingLayout`, `MessageBox` (item), `SaveSequence`, `CustomTrigger`, `FindHome`, `SwitchProfile` |

**Editor-only, replaced or removed** (5):
- `Editing/SequenceEditContext.cs` and `Editing/SequencePropertyCapture.cs` are replaced by trimmed copies (§2.5).
- `Editing/SequenceEditBindingResolver.cs` and `Editing/PendingSequenceEdit.cs` are removed. Only `SequenceEditContext.cs` and `Behaviors/SequenceEditBehavior.cs` use them.
- `Logic/ExprConverter.cs` (a XAML `IMultiValueConverter`) is removed.

**Stray `using` lines must be handled.** `Sequencer.cs:16,20,23` import `…SequenceItem.Autofocus`, `…SequenceItem.Focuser` and `…Trigger.MeridianFlip`, so dropping those folders breaks the build. Two fixes work:
- One mac file of namespace anchors (`namespace NINA.Sequencer.Trigger.MeridianFlip { internal static class MacNamespaceAnchor { } }`, and so on); verified.
- Upstream patch P1. It is verified too, and also removes the need to reference ASCOM.Com, nikoncswrapper, Accord.IO, Google.Protobuf and Dasync from the sequencer (§6.7).

### 1.6 Compile-probe evidence

Method (Appendix A):
1. Build upstream `NINA.Sequencer.csproj` and its dependencies for `net10.0-windows` (`EnableWindowsTargeting=true`) in a `/private/tmp` copy. It succeeded on this Mac in 35.7 s with 0 errors and 1533 warnings.
2. Compile a throwaway `net10.0` project, `AssemblyName=NINA.Sequencer` with no WPF, that links the upstream sources. It references the Windows-built dependency DLLs plus a throwaway inert stand-in assembly.

| Round | Change | Unique errors |
|---|---|---|
| 1 | all 234 files, only base packages | 1258 lines (generator assembly not referenced → CS9248 etc.) |
| 2 | reference `NINA.Sequencer.Generators.dll` as an assembly too | 126 |
| 3 | transitive packages added (stray `using`s) | 101 (declaration-level WPF only) |
| 4 | minimal stand-ins (GeometryGroup, ICollectionView, ModifierKeys, …) | 51 |
| 5 | rig exclusions (controllers also excluded at first, so `TemplatedSequenceContainer` went missing) | 51 |
| 6 | controllers kept, namespace anchors, fuller inert stand-ins (Dependency*, Binding*, Dispatcher, …) | 98 (now method bodies: Editing binding API such as `MultiBindingExpression`/`DependencyPropertyDescriptor`, `WeakEventManager`, `Application`, `CommandManager`, …) |
| 7 | Editing: 4 WPF-binding files excluded, 2 trimmed copies added; stand-ins extended | 11 → 5 |
| final | rig set | **5**, all CS0012 at `Sequencer.cs:115`, `SequenceRootContainer.cs:58,66`, `Center.cs:169`, `SolveAndSync.cs:98` (Windows `NINA.Core.dll` signatures typed with PresentationFramework enums) |
| full | every item except `TakeSubframeExposure.cs` | the same 5 artefacts (+ 4 more of the same kind in the dropped `RunAutofocus`/`CenterAndRotate`/`SolveAndRotate`/`MessageBox`) + 3 stand-in gaps (`SaveFileDialog`, `OpenFileDialog.CheckFileExists`, a `Dispatcher.InvokeAsync` overload) |

With the probe's copies of the `using` lines deleted (P1), the rig set compiled the same way: the same 5 artefacts and nothing else. That build had no ASCOM, Accord, Google.Protobuf, Dasync, EF6 or nikoncswrapper references.

Limits: no emitted DLL and no run-time test, because the probe mixed Windows-built dependencies with stand-ins. The upstream source generator ran from its `netstandard2.0` build and generated code for `MaxAltitudeCondition`. The probe compiled the WPF.Base slice and the sequencer separately. A merged build reached only declaration-level type-identity errors, so its method bodies were not verified.

---

## 2. Discovery and instantiation, and the static catalogue

### 2.1 How upstream does it

1. **MEF exports.** Each entity carries `[Export(typeof(ISequenceItem|ISequenceCondition|ISequenceTrigger|ISequenceContainer))]` and `[ExportMetadata("Name"|"Description"|"Icon"|"Category", …)]`. Its MEF constructor is marked `[ImportingConstructor]`, for example `Center.cs:47-87` and `DitherAfterExposures.cs:42-64`. Containers often export twice, as both `ISequenceItem` and `ISequenceContainer` (`DeepSkyObjectContainer.cs:54-55`).
2. **`NINA.Plugin/PluginLoader.cs`** composes the core and plugins:
   - Constructor (`:65-158`): takes 40 services and builds the 10 `IDateTimeProvider`s itself (`:145-156`).
   - `Load` (`:262-266`): builds a `TypeCatalog` from every class in a `NINA.Sequencer*` namespace (`GetCoreSequencerTypes`, `:684-702`) plus equipment SDK providers.
   - `Compose` (`:529-566`): creates a `CompositionContainer` with `ComposeExportedValue` for every service (`:567-610`) and imports `PartsImport` (`:808-836`).
   - WPF parts: `DataTemplateImports` (`[ImportMany(typeof(ResourceDictionary))]`, `:825-826`) are merged into `Application.Current.Resources` (`:535-537`). `AssignSequenceEntity` (`:725-760`) sets Name, Description and Category through `Loc` for `Lbl_` keys, **casts the icon from the app resource dictionary to `GeometryGroup`** (`:744-747`), sets `item.SymbolBroker` (`:752`), and sorts by Category+Name (`:759`).
3. **`SequencerFactory`** (`SequencerFactory.cs:46-108`) keeps the prototype lists and builds 4 WPF `ICollectionView`s for the sidebar through `CollectionViewSource` (`:81-105`). `GetItem<T>()` and its siblings return `Clone()` of the first prototype of exactly that type (`:150-164`). The app creates it after `pluginProvider.Load()` (`NINA/ViewModel/Sequencer/SequenceNavigationVM.cs:102-104`).
4. **Item templates.** Each item's UI is a `DataTemplate` in a `Datatemplates.xaml` ResourceDictionary, chosen by `View/SequenceDataTemplateSelector.cs`. Headless code needs none of this.
5. **Source generator** (`NINA.Sequencer.Generators`, `netstandard2.0`): for `[UsesExpressions]` classes it generates `Clone()`, expression-backed partial properties, validation (`GenerateValidation = true`) and proxies (`NINA.Sequencer.Generators/ARCHITECTURE.md`). The attribute types are declared **in the generator assembly itself** (`ExpressionGenerator.cs:341,384`). The consuming project must therefore reference it as an analyzer *and* as a normal assembly. Upstream does both through `ProjectReference … OutputItemType="Analyzer"`, which keeps the default `ReferenceOutputAssembly=true` (`NINA.Sequencer.csproj:55`). The probe reproduced the failure mode: CS9248/CS0759 everywhere until both references were present.

### 2.2 Design: a static, WPF-free catalogue

```csharp
// mac/src/NINA.Mac.Sequencing/Catalogue/RigSequencerCatalogue.cs (sketch)
public sealed class RigSequencerCatalogue {
    public IList<ISequenceItem> Items { get; } = new List<ISequenceItem>();
    public IList<ISequenceCondition> Conditions { get; } = new List<ISequenceCondition>();
    public IList<ISequenceTrigger> Triggers { get; } = new List<ISequenceTrigger>();
    public IList<ISequenceContainer> Containers { get; } = new List<ISequenceContainer>();
    public IList<IDateTimeProvider> DateTimeProviders { get; }

    public RigSequencerCatalogue(HeadlessServices s) {
        // PluginLoader.cs:145-156, same order (TimeCondition/WaitForTime pick providers by type)
        DateTimeProviders = new List<IDateTimeProvider> {
            new TimeProvider(s.NighttimeCalculator), new SunsetProvider(s.NighttimeCalculator), new CivilDuskProvider(s.NighttimeCalculator),
            new NauticalDuskProvider(s.NighttimeCalculator), new DuskProvider(s.NighttimeCalculator), new DawnProvider(s.NighttimeCalculator),
            new NauticalDawnProvider(s.NighttimeCalculator), new CivilDawnProvider(s.NighttimeCalculator), new SunriseProvider(s.NighttimeCalculator),
            new MeridianProvider(s.ProfileService) };
        // Direct construction = compile-time check of upstream ctor signatures.
        Add(new Center(s.ProfileService, s.Telescope, s.Imaging, s.FilterWheel, s.Guider, s.Dome, s.DomeFollower, s.PlateSolverFactory, s.WindowServiceFactory));
        Add(new DitherAfterExposures(s.Guider, s.ImageHistory, s.ProfileService, s.SafetyMonitor));
        Add(new DeepSkyObjectContainer(s.ProfileService, s.NighttimeCalculator, s.FramingAssistant, s.Application, s.PlanetariumFactory, s.Camera, s.FilterWheel, s.SymbolBroker));
        Add(new MaxAltitudeCondition(s.ProfileService));
        // ... the rest of the §1.5 list
        Sort(); // PluginLoader.cs:759 order: Category + Name
    }

    private void Add(object entity) {
        // Registration lists come from the type's own [Export(typeof(X))] attributes, so dual-exported
        // containers land in both lists as separate prototypes, exactly like MEF (one instance per export).
        // Name/Description/Category from [ExportMetadata] via upstream SequenceEntityExtension.AddMetaData
        // (Utility/SequenceEntityExtensions.cs:19-51) with a headless IApplicationResourceDictionary that returns
        // null for icon keys; containers additionally get Name (AddMetaData(ISequenceContainer) skips it, PluginLoader.cs:733-736 does not).
        // entity.SymbolBroker = symbolBroker  (PluginLoader.cs:752)
    }
}
```

- **Factory.** `HeadlessSequencerFactory : ISequencerFactory` has the four `ICollectionView` properties return `null`. Precedent: upstream test `CorpusSequencerFactory`, `LegacySequenceMigrationCorpusTest.cs:1206-1265`. `GetX<T>` is copied from `SequencerFactory.cs:150-164`.
  - The `GetX<T>` methods **must stay public instance methods.** The JSON converters find them by reflection on the concrete type: `Factory.GetType().GetMethod(nameof(Factory.GetItem))`, in `SequenceItemCreationConverter.cs:50`, `SequenceConditionCreationConverter.cs:34`, `SequenceTriggerCreationConverter.cs:34` and `SequenceContainerCreationConverter.cs:36`. An explicit interface implementation returns `null` there, and every entity would load as Unknown.
  - Reusing upstream `SequencerFactory` would also work, but only if the compat `CollectionViewSource.GetDefaultView` returns a working view: `SequencerFactory.cs:81-84` calls `ItemsView.GroupDescriptions.Add` at once. The headless factory avoids depending on that.
- **Why not headless MEF?** `System.ComponentModel.Composition` runs on macOS, and a `TypeCatalog` of allow-listed types would reproduce upstream injection exactly. Its failures appear at run time, though (`CompositionOptions.DisableSilentRejection`). Direct `new` calls fail at compile time when upstream changes a constructor, which is the signal a downstream fork needs. Keep MEF as a fallback if the catalogue grows past about 60 entries.
- **Drift guards** (unit tests):
  1. The catalogue equals the expected list.
  2. Every type in `NINA.Sequencer.dll` with `[Export]` is either catalogued or listed in `DroppedOrCompiledOnly` with a reason, so new upstream items show up at merge time.
  3. Every catalogued prototype has a non-empty Name and Category.
  4. `GetItem<T>()` returns a distinct clone.

### 2.3 The source generator on macOS

Add `mac/src/NINA.Sequencer.Generators.Mac/NINA.Sequencer.Generators.Mac.csproj`:
- It links `$(NinaRoot)NINA.Sequencer.Generators/*.cs` and the two `AnalyzerReleases.*.md` files as `AdditionalFiles`.
- It overrides `TargetFramework=netstandard2.0`, sets `RuntimeIdentifier` empty and `EnforceExtendedAnalyzerRules=true`, and references `Microsoft.CodeAnalysis.CSharp` 5.6.0. The upstream csproj pins `Microsoft.CodeAnalysis.CSharp` 5.6.0, and the user-local SDK 10.0.401 loaded the analyzer (the probe ran it).
- `NINA.Sequencer.Mac` and `NINA.Mac.Sequencing` both reference it with `OutputItemType="Analyzer"`, keeping `ReferenceOutputAssembly` true.

The alternative is referencing the upstream csproj directly. That writes `bin/obj` into the upstream tree, and the mac layout avoids that.

### 2.4 Assembly naming

| Project | AssemblyName | Why |
|---|---|---|
| `NINA.Sequencer.Mac` | **`NINA.Sequencer`** | `$type` strings in every saved sequence and template; `JsonCreationConverter.GetType` (`:212-224`) |
| `NINA.WPF.Base.Mac` | `NINA.WPF.Base` | consistent with `NINA.Core.Mac` → `NINA.Core`; nothing serialises these types |
| `NINA.Mac.Sequencing` | `NINA.Mac.Sequencing` | new rig entities serialise as `…, NINA.Mac.Sequencing`; Windows NINA loads them as Unknown |

### 2.5 The two trimmed Editing copies

Both live in `mac/src/NINA.Sequencer.Mac/Replaced/Editing/`, keep the MPL header, and cite upstream.

- **`SequenceEditContext.cs`.** This is the upstream file minus members only XAML uses:
  - the attached properties `CommandProperty`/`OperationProperty` (`:30-42`) and `HistoryProperty`/`IsRecordingEnabledProperty` (`:69-86`)
  - `StepExpression` (`:128-136`)
  - `HistoryCommand` (`:147-157`)

  It keeps `CreateCommand`, `SelfRecordingCommand`, `ExecuteCommand`, `Register`/`Unregister`/`Find`, `Placement`, `Structure`, `Target`, `Property` and `Toggle` verbatim; those are WPF-free.

  Behaviour is identical headless. Sessions are registered only by `SequenceEditHistory`, which only `Sequence2VM` creates, so `Find(...)` returns `null` and every helper just runs its action, as upstream does when no editor is open (`:116-127`).
- **`SequencePropertyCapture.cs`.** This is upstream minus `Create(ISequenceEntity, BindingExpression)` (`:51-124`) and `FindPath` (`:163-185`), which only `Create` uses. It keeps `ISequenceEditCapture`, `Capture<T>`, `Target` and `BoundSnapshot`.

Both prototypes compiled in the probe. A unit test should store the SHA-256 of the two upstream files and fail when upstream changes them, so the copies get re-synced on merge. Upstream patch P4 (§6.7) would make the copies unnecessary.

---

## 3. JSON (de)serialisation and loading existing files

- **Writing.** `SequenceJsonConverter.Serialize` (`:43-49`) uses `TypeNameHandling.All`, `PreserveReferencesHandling.All` and a contract resolver that hides linked-template runtime state (`:65-93`). Entities are `[JsonObject(MemberSerialization.OptIn)]`, so only `[JsonProperty]` members are written. WPF-typed members such as `Icon` and the `ICommand` properties are never serialised.
- **Reading.** `Deserialize` (`:56-62`) registers 5 creation converters.
  - Each reads `$type`, resolves it with `Type.GetType`. Failing that it retries with the legacy `, NINA` → `, NINA.Sequencer`/`NINA.Core`/`NINA.Astrometry` rewrite (`JsonCreationConverter.cs:212-224`); the bundled example templates still use `, NINA`.
  - It then asks the factory for a **clone of the catalogued prototype** (`GetItem<T>()` through reflection), applies optional plugin upgraders and `Populate`s it (`:166`).
  - A type that isn't loaded, or has no prototype, becomes `UnknownSequenceItem`, `UnknownSequenceCondition`, `UnknownSequenceTrigger` or `UnknownSequenceContainer`. Examples: `SequenceItemCreationConverter.cs:31-62`, `SequenceTriggerCreationConverter.cs:26-46`.
  - Date-time providers are matched by type against `factory.DateTimeProviders` (`SequenceDateTimeProviderCreationConverter.cs:33-34`). **The catalogue must supply all 10.** Otherwise `TimeCondition.SelectedProvider` deserialises to `null`.
- **Can existing NINA sequences and templates be loaded? Yes**, given:
  1. the assembly is named `NINA.Sequencer`;
  2. the dependencies keep their upstream names (`NINA.Core`, `NINA.Astrometry`, …), which the M3 projects already do;
  3. the 10 date-time providers are present.

  At run time, `UnknownSequenceItem.Execute` throws `SequenceItemSkippedException`, so the item is skipped. `Validate()` returns `false` with an "unknown instruction" issue (`SequenceItem/UnknownSequenceItem.cs:34-55`). The headless runner shows these issues before starting (§4.4).
- **Expected result for the bundled templates** (`NINA/Sequencer/Examples/*.template.json`):
  - *Startup* and *End* load fully: `CoolCamera`, `UnparkScope`, `WarmCamera`, `ParkScope`, `ExternalScript`, `Annotation`, parallel and sequential containers.
  - *Target* loads with 5 Unknown items (3× `RunAutofocus`, `StartGuiding`, `CenterAndRotate`) and 3 Unknown triggers (`MeridianFlipTrigger`, 2 autofocus triggers). That is the intended outcome for this mount.
- **Fixtures:**
  - the three templates above;
  - upstream's corpus `NINA.Test/Sequencer/Serialization/LegacySequences/v3.2/master-3.2-all-sequence-entities.sequence.json` (200 entities, with a manifest);
  - round-trip `Serialize`→`Deserialize` of every generated night tree.
- **Linked templates.** With `LinkedTemplateContainer` compiled but not catalogued, `GetContainer<LinkedTemplateContainer>()` returns `null` and the subtree becomes `UnknownSequenceContainer`. That's acceptable for M7. Catalogue it later together with a `TemplateLinkResolver` and a `TemplateController` that is null-safe without `Application.Current` (P3).
- **Sharing with Windows NINA.** Trees made only of upstream entities load in Windows NINA unchanged. A `MaxAltitudeCondition` loads there as Unknown. Offer an "export for Windows" option that writes the `LoopWhile Mount_Altitude <= N` form instead (§5.5).
- **Upstream observation, not exercised here.** In `JsonCreationConverter.ReadJson`'s catch block (`:196-203`), `switch (objectType) { case ISequenceTrigger: … }` type-tests a `System.Type` instance. It therefore always falls to `UnknownSequenceItem`, even for triggers and conditions. A trigger that throws during `Populate` would then put an item into a trigger list. It is only reached on exceptions; worth an upstream issue.

---

## 4. Runtime dependencies: the headless implementation set

### 4.1 What the rig entities ask for

From the `[ImportingConstructor]` signatures (§2.1) and the call sites read:

| Service | Used by (rig) | Headless implementation |
|---|---|---|
| `IProfileService` | almost all | upstream `ProfileService` (`NINA.Profile.Mac`, M3) |
| `ICameraMediator` | `TakeExposure`, `CoolCamera`, `WarmCamera`, `DewHeater`, `DeepSkyObjectContainer`, `CenterAfterDrift`, `ReconnectOnDownloadFailure` | upstream `CameraMediator` (slice) + handler `CameraVM` (upstream, compiles) over an `ICamera`: ASI driver (M1/M3) or the sim camera |
| `ITelescopeMediator` | `Center`, `SolveAndSync`, `Slew*`, `Park`/`Unpark`, `SetTracking`, `CenterAfterDrift`, `SymbolBroker` | `TelescopeMediator` + `TelescopeVM` over `Lx200Telescope` (M4) or the sim mount. Profile must set `TelescopeLocationSyncDirection` ≠ `PROMPT`, else `TelescopeVM.cs:425-435` opens a dialog through `WindowService.ShowDialog` |
| `IGuiderMediator` | `Dither`, `DitherAfterExposures`, `Center` (`StopGuiding` at `Center.cs:171`) | `GuiderMediator` + `GuiderVM` over `DirectGuider` (`DirectGuider.cs:195-209` start/stop just flip a flag; dither pulses the mount, `:228-275`) |
| `IFocuserMediator` | `SymbolBroker`, `ConnectAll` | `FocuserMediator` + `FocuserVM` over `Lx200Focuser` (M4), or the disconnected handler |
| `IImagingMediator` | `TakeExposure` (`CaptureImage`, `PrepareImage`), `CaptureSolver.cs:55` (`CaptureAndPrepareImage`), `SymbolBroker` (`ImagePrepared`) | `ImagingMediator` + **upstream `NINA/ViewModel/ImagingVM.cs` linked unchanged**. It is `internal` but WPF-light, and its `AddMetaData` (`:162-195`) fills the FITS metadata. It needs a headless `IImageControlVM` whose `PrepareImage` returns a raw-only `IRenderedImage`: `RawImageData` set, `Image` and `GetThumbnail()` = null, until M5 brings real rendering and HFR. Plus a headless `IImageStatisticsVM`. |
| `IImageSaveMediator` | `TakeExposure`/`SmartExposure`/`TakeManyExposures` (`Enqueue`), `PlatesolvingImageFollower` (`BeforeImageSaved`) | `ImageSaveMediator` + **upstream `NINA/ViewModel/ImageSaveController.cs` linked unchanged** (`BaseVM`, `IImageSaveController`; save path `:90-121`) |
| `IImageHistoryVM` | `TakeExposure` (`Add`, `PopulateStatistics`), `ImagingVM` (`GetNextImageId`), `DitherAfterExposures` (`ImageHistory.Count`) | `HeadlessImageHistory`: a port of the counting core of `NINA/ViewModel/ImageHistory/ImageHistoryVM.cs:238-320` (`ImageHistory` list, `Interlocked` id, `Add`, `PopulateStatistics`, `AppendImageProperties`). It is a functional component, not a null object, because dithering depends on the count. |
| `IApplicationStatusMediator` | `CenterAfterDrift`, `PlatesolvingImageFollower`, `ImagingVM`, `ImageSaveController` | `ApplicationStatusMediator` + `HeadlessApplicationStatus : IApplicationStatusVM` that raises an event for the UI and log |
| `ISymbolBroker` | every entity (`item.SymbolBroker`), `DeepSkyObjectContainer`, `ExternalScript` | upstream `SymbolBroker` (`Logic/SymbolBroker.cs:132-171`: 13 mediators; registers itself as consumer on all of them) |
| `INighttimeCalculator` | `DeepSkyObjectContainer` (`Task.Run(Calculate)` in the ctor), the 9 sun-based providers | upstream `NINA.Astrometry/NighttimeCalculator.cs` (M3) |
| `IPlateSolverFactory` | `Center`, `SolveAndSync` | `PlateSolverFactoryProxy` (M6), or `SimPlateSolverFactory` in tests. **`CenterAfterDriftTrigger.cs:141-142` and `PlatesolvingImageFollower.cs:136-137` bypass injection and use the static `PlateSolverFactory`.** |
| `IWindowServiceFactory` | `Center`, `SolveAndSync` | no-op factory from `NINA.Core.Mac`. The concrete `WindowServiceFactory` must be headless-safe, because `CenterAfterDriftTrigger.cs:142` news it. |
| `IFramingAssistantVM`, `IApplicationMediator`, `IPlanetariumFactory` | `DeepSkyObjectContainer` UI commands only | null objects (`SetCoordinates` → `false`; `ChangeTab` no-op; `GetPlanetarium` → null) |
| `IFilterWheelMediator`, `IDomeMediator`, `IDomeFollower`, `IRotatorMediator`, `IFlatDeviceMediator`, `ISafetyMonitorMediator`, `ISwitchMediator`, `IWeatherDataMediator` | constructor parameters everywhere; `Center.DoCenter` reads `domeMediator.GetInfo().Connected` (`Center.cs:124-125`); `DitherAfterExposures.cs:96` and `CenterAfterDriftTrigger.cs:191` read `safetyMonitorMediator.GetInfo()` | upstream mediators + one generic **`DisconnectedDeviceVM<TInfo>`** handler per type (`GetDeviceInfo()` → `new TInfo { Connected = false }`; `Connect()` → `false`). `DomeFollower` null object: `IsFollowing = false`, `TriggerTelescopeSync()` → `true`. |
| `ISequenceMediator` | `ReconnectOnDownloadFailure`, `WaitUntil` | upstream `Mediator/SequenceMediator.cs` + a minimal `ISequenceNavigationVM` owned by the runner |
| `IList<IDateTimeProvider>` | `TimeCondition`, `WaitForTime`, `ResetVariableToDate` | the catalogue's list (§2.2) |
| `ITemplateLinkResolver` | `LinkedTemplateContainer` (not catalogued) | none in M7 |

### 4.2 Rules the probe and reading turned up

1. **Every mediator needs a registered handler, including absent devices.**
   - `DeviceMediator.GetInfo()` returns `default`, i.e. `null`, without one (`NINA.WPF.Base/Mediator/DeviceMediator.cs:111-116`), and `Center.cs:124-125` dereferences it.
   - Its `Connected`/`Disconnected` event accessors and `ImageSaveMediator`'s events forward to `this.handler` without a null check (`DeviceMediator.cs:38-45`, `ImageSaveMediator.cs:45-63`).
2. **Register handlers before building or loading any tree.**
   - `SymbolBroker`'s constructor subscribes to every mediator.
   - `PlatesolvingImageFollower` subscribes to `BeforeImageSaved` in `SequenceBlockInitialize`.
   - `ReconnectOnDownloadFailure` subscribes to `cameraMediator.DownloadTimeout`.
3. **`WeakEventManager` must work, not just compile.** `DeepSkyObjectContainer`'s target `CoordinatesChanged` wiring (`:150-183`) keeps conditions' and triggers' inherited coordinates in sync. A strong-reference stand-in is acceptable headless, since containers live all night.
4. **Interactive prompts on the run path.**
   - The sequencer itself avoids its prompt via `Start(..., skipIssuePrompt: true)` (`Sequencer.cs:78`).
   - The device VMs prompt only from UI commands such as Disconnect (`CameraVM.cs:632-637`, `TelescopeVM.cs:721-727`, `FocuserVM.cs:423-428`), or from the lat/long sync prompt the profile switches off.
   - `NINA.Core.Mac`'s `MyMessageBox` must still return a defined default, never block.
5. **Clocks.** Conditions and waits call `DateTime.Now` directly: 35 calls in 11 files under `Conditions/` and `SequenceItem/Utility/`, plus 11 in `Utility/ItemUtility.cs`. Only the date-time providers, `TimeCondition`, `TimeSpanCondition` and `WaitForTime` have an `ICustomDateTime` seam. Simulated nights must therefore run in real time, with thresholds placed relative to *now* (§6.4).

### 4.3 Simulation pieces (M7 test harness)

| Piece | Description |
|---|---|
| `SimCamera : ICamera` | 1920×1080 RGGB frames (reuse `mac/src/NINA.Mac.ZwoProbe/ProbeFits.cs`'s synthetic pattern), `CanSetTemperature`, cooler ramp, 1–3 s exposures |
| `SimMount : ITelescope` | Alt/az computed from RA/Dec and the clock with `NINA.Astrometry`; instant slews; `PulseGuide` moves the pointing by the guide rate × duration so `DirectGuider` dithers are measurable. Alternatively M4's `Lx200Telescope` over its serial simulator, which exercises the real driver. |
| `SimPlateSolverFactory` | Returns the mount position plus a configurable error. It feeds `Center`'s injected factory. `CenterAfterDrift` needs M6's solver with a fake `astap` executable, or P2. |

### 4.4 Runner

`HeadlessSequenceRunner`:
1. Validate. The walk is ported from the private `Sequencer.Validate` (`Sequencer.cs:178-214`), so issues and Unknown placeholders can be shown without `MyMessageBox`.
2. `new Sequencer(root).Start(progress, token, skipIssuePrompt: true)`.
3. Expose `CancellationTokenSource`, current item and status events.

`Sequencer.Start` already calls `Initialize` and `Teardown` on every entity (`Sequencer.cs:78-95`).

---

## 5. Target form → sequence tree, MaxAltitude condition, field rotation

### 5.1 Plan model (fork code, `NINA.Mac.Sequencing.Planning`)

```csharp
public sealed record TargetPlan(
    string Name, Coordinates J2000,                 // from DSO search (M3 DB fix) or manual entry
    double ExposureSeconds, int Gain, int Offset, short Bin = 2,
    int? Count = null,                              // null = until a stop condition fires
    int DitherEvery = 5,                            // DitherAfterExposures.AfterExposures; 0 = off
    double RecenterArcmin = 1.5, int RecenterEvery = 5, // CenterAfterDriftTrigger
    double HorizonOffsetDeg = 0, double? MinAltitudeDeg = null,
    double MaxAltitudeDeg = 75,                     // plan decision 5 default
    KeyholePolicy Keyhole = KeyholePolicy.Skip,     // Skip | WaitUntilBelow
    FieldRotationPolicy FieldRotation = FieldRotationPolicy.Warn, // plan decision 4 default
    double BlurTolerancePx = 1.0);

public sealed record NightPlan(
    IReadOnlyList<TargetPlan> Targets,
    double? CoolToC = 0, TimeSpan? CoolDuration = null, bool DewHeater = true,
    DawnStop Dawn = DawnStop.Astronomical, int DawnOffsetMinutes = 0,
    bool WarmAtEnd = true, bool ParkAtEnd = true, bool ReconnectOnDownloadFailure = true,
    string SirilScript = null);                     // ExternalScript in the end area (decision 8)
```

### 5.2 Generated tree

The shape follows upstream's own target template (`NINA/Sequencer/Examples/Basic Sequence Target.template.json`) and `SimpleDSOContainer.TransformToDSOContainer` (`SimpleDSOContainer.cs:758-795`) with `CreateStartupContainer` (`:617-669`). From those it drops the autofocus, rotator, guiding and meridian-flip entries and adds the stops.

```
SequenceRootContainer "Night 2026-10-05"
├─ [Trigger] ReconnectOnDownloadFailure                (optional)
├─ StartAreaContainer                                  ≈ SimpleStartContainer.cs:30-49
│   ├─ DewHeater(on)                                   only if CameraInfo.HasDewHeater
│   ├─ CoolCamera(T, duration)
│   └─ UnparkScope
├─ TargetAreaContainer
│   └─ DeepSkyObjectContainer "M83"                    Target = InputTarget(lat, lon, horizon){Name, J2000}; NO conditions → runs once
│       ├─ SequentialContainer "Prepare"
│       │   [Conditions] LoopCondition(1) ∧ TimeCondition(dawn±offset) ∧ MaxAltitudeCondition(max)*
│       │   ├─ WaitUntilAboveHorizon(offset)           waits for rise; the dawn watchdog interrupts it
│       │   ├─ WaitForAltitude(">", min)               only if MinAltitudeDeg
│       │   ├─ WaitForAltitude("<", max)               only if Keyhole == WaitUntilBelow (then the * condition is omitted)
│       │   └─ Center                                  coordinates inherited from the DSO container
│       └─ SequentialContainer "Imaging 10s×N"         ≈ the template's "20x300s" block
│           [Conditions] LoopCondition(N)? ∧ TimeCondition(dawn±offset) ∧ AboveHorizonCondition(offset)
│                        ∧ MaxAltitudeCondition(max) ∧ [AltitudeCondition(min)] ∧ [LoopWhile FieldRotation_MaxSub >= T]**
│           [Triggers]   DitherAfterExposures(k) ∧ CenterAfterDriftTrigger(arcmin, every n)
│           └─ TakeExposure(T, bin 2, gain, offset, LIGHT)
└─ EndAreaContainer
    ├─ WarmCamera(duration)  → DewHeater(off)          ≈ SimpleEndContainer.cs:30-49
    ├─ ParkScope                                       soft park in the M4 driver; never :hP#
    └─ ExternalScript(siril-cli …)                     optional
```
\*\* only when `FieldRotationPolicy == Stop` (§5.6).

Upstream execution semantics this relies on:

| Semantic | Source | Consequence for the tree |
|---|---|---|
| A container *with* conditions loops while **all** of them are true | `SequenceContainer.cs:270-288`; `SequentialStrategy.cs:37-67` | no conditions on the DSO container, or `Center` would rerun every pass |
| Without conditions a container runs exactly once (`Iterations < 1`) | `SequentialStrategy.cs` `CanContinue` | |
| Ancestor conditions are AND-ed into every descendant's continue check | `SequentialStrategy.cs` `CanContinue`, recursion on `Parent` | |
| Ancestor triggers run before every descendant item | `RunTriggers`, recursion on `Parent` | dither and recentre triggers on the Imaging block see each `TakeExposure` |
| `LoopCondition(1)` plus stop conditions gives a single pass that aborts when a stop fires | | used for "Prepare" |
| Watchdogs interrupt the running item mid-exposure: altitude-type conditions every 5 s, `TimeCondition` every 1 s, `LoopWhile` every 5 s | `LoopForAltitudeBase.cs:26,42-49`; `TimeCondition.cs:56`; `LoopWhile.cs` | |

The generator constructs everything through `factory.GetContainer<T>()`, `GetItem<T>()`, `GetCondition<T>()` and `GetTrigger<T>()` and sets the generated scalar properties. Upstream `SimpleExposure.TransformToSmartExposure` does the same (`SimpleExposure.cs:121-142`). The generator sets `DeepSkyObjectContainer.Target` **before** adding children, as `TransformToDSOContainer` does (`:759-765`), so `AfterParentChanged` picks up context coordinates (`AltitudeCondition.cs:110-131`).

`TimeCondition`: the generator sets `SelectedProvider` to the `DawnProvider` (astronomical) or `NauticalDawnProvider` instance *from the factory's list*, never a new one, and sets `MinutesOffset`.

### 5.3 Generation-time validation (`PlanValidator`)

`PlanValidator` uses only `NINA.Astrometry`:
- `Coordinates.Transform`, the profile's `CustomHorizon`, `NighttimeCalculator`
- `ItemUtility.CalculateTimeAtAltitude` (`Utility/ItemUtility.cs:363-…`) for each target

It reports:
- rise and set over the custom horizon;
- transit time and altitude;
- the keyhole interval where altitude > max, with a warning if the target spends its whole window there;
- the field-rotation max sub at window start, end and transit, against `ExposureSeconds` (§5.6);
- overlaps between consecutive targets' windows, and targets that never rise before dawn.

These are warnings, not errors, consistent with plan decision 4. The plan §9 decision on max altitude defaults to a fixed 75°.

### 5.4 Why a new condition instead of `AltitudeCondition` or `LoopWhile`

- **`AltitudeCondition` is a lower bound.** Its `Check` returns `Data.IsRising || Data.CurrentAltitude >= Offset` (`AltitudeCondition.cs:142`). Its expected-time maths special-cases the type name (`mustSet = data.Name == "AltitudeCondition"`, `ItemUtility.cs:327`).
- **`LoopWhile` with `Mount_Altitude <= 75` should work today**, per `rig_verify_mvp` MVP-12 (not run here); note the `_` delimiter (`SymbolBroker.cs:119`). It has three drawbacks:
  - It uses the mount's reported altitude, so it does nothing before the slew and can't predict.
  - It **throws** while the symbol is uninitialised, e.g. mount disconnected (`LoopWhile.cs` `Check`: `throw new SequenceEntityFailedException`), which fails the container.
  - It has no expected-time display.

### 5.5 `MaxAltitudeCondition`

This is a new file in `NINA.Mac.Sequencing/Conditions/`, MPL, citing `AltitudeCondition.cs:36-230`. A compiled prototype is in Appendix A.

- `[Export(typeof(ISequenceCondition))]` with `[ExportMetadata]` (Name, Description, Icon `WaitForAltitudeSVG`, Category `Lbl_SequenceCategory_Condition`) and `[JsonObject(OptIn)]`.
- Declared as `[UsesExpressions(GenerateValidation = true)] public partial class MaxAltitudeCondition : LoopForAltitudeBase, IValidatable`. `LoopForAltitudeBase` lives in namespace `NINA.Sequencer.SequenceItem.Utility` (`Conditions/LoopForAltitudeBase.cs:7`).
- The constructor calls `base(profileService, useCustomHorizon: false)` and sets `Data.Offset = 75` and `Data.Comparator = GREATER_THAN`.
  - `GREATER_THAN` with `until: true` makes `CalculateExpectedTimeCommon` (`ItemUtility.cs:303-352`) predict when the target *climbs above* the limit, which is the stop time.
  - The class name differs from "AltitudeCondition", so `mustSet` is `false`.
- The property is `[IsExpression(Default = 75, Range = [0, 90], Proxy = "Data.Offset")] public partial double MaxAltitude { get; set; }`.
- `Check` is `Data.CurrentAltitude <= MaxAltitude`, with **no rising or setting exemption**. In alt-az the zenith hole is unsafe in both directions: azimuth rate diverges and field rotation peaks at transit.
- Coordinates are inherited from the DSO context through `ItemUtility.RetrieveContextCoordinates`, exactly like `AltitudeCondition.AfterParentChanged` (`:110-131`) and `Check` (`:133-142`). Validation reports an issue when there is no DSO parent. The watchdog comes from the base class: every 5 s it calls `Parent.Interrupt()` when `Check` fails.
- Semantics:
  - target already above max at the start of the Imaging block: the block finishes immediately;
  - target rises through max mid-exposure: the exposure is interrupted within 5 s and the block ends;
  - `KeyholePolicy.WaitUntilBelow` adds upstream `WaitForAltitude` with `AboveOrBelow = "<"`, which waits until altitude ≤ offset (`WaitForAltitude.cs:78-111`), before `Center`, to image the western side after transit.
- Defence in depth (M9): a slew guard in the telescope service, and the mount's own `:So` limit at about 75–80° (plan §6).

### 5.6 Field rotation: where it enters

**Formula.** For an alt-az mount at latitude φ, the field rotation rate is ω_FR = ω⊕ · cos φ · cos A / cos h, with A measured from north and ω⊕ = 7.2921150×10⁻⁵ rad/s.

The longest sub keeping corner blur ≤ B pixels is t = B / (|ω_FR| · r), where r = ½·√(W² + H²) in binned pixels: r = 1101.4 px for 1920×1080.

The C# prototype `FieldRotationCalculator` reproduces every cell of the plan §6 table:

| Dec | HA | Max sub |
|---|---|---|
| −30 | 0 h | 10.6 s |
| −30 | 3 h | 16.6 s |
| 0 | 0 h | 5.1 s |
| 0 | 3 h | 28.7 s |
| +10 | 0 h | 2.9 s |
| +10 | 3 h | 64.7 s |

That was run with `mac/dotnet run`; output in Appendix A. The result does not depend on focal length, so it holds with or without the reducer.

**It enters in three places:**
1. **Generation time** (`PlanValidator`). For each target window, compare `ExposureSeconds` with the max sub along the target's path, and report when and where it is exceeded. Example: "M42 10 s subs exceed the 1 px limit from 00:40 to 02:10 around transit."
2. **Run time, as a symbol.** `FieldRotationSymbols : ITelescopeConsumer, ICameraConsumer` registers through the public API, `symbolBroker.RegisterSymbolProvider("FieldRotation")` (`Logic/ISymbolBroker.cs:28`, `SymbolBroker.cs:835-850`). On each `TelescopeInfo` update it publishes:
   - `FieldRotation_RateDegPerMin`
   - `FieldRotation_MaxSub`: seconds, at the current binning and `BlurTolerancePx`
   - `FieldRotation_Ok`: 1 or 0 for the current planned sub

   It reads the mount's alt/az, which in alt-az is the real pointing, and binning and size from `CameraInfo`.
3. **Policy per target**, chosen in the Target form:
   - **Warn** (default, decision 4): nothing is added to the tree. The UI shows `FieldRotation_MaxSub` live, and the validator warnings stand.
   - **Stop**: add `LoopWhile` with predicate `FieldRotation_MaxSub >= T` to the Imaging block. It stops the target when rotation outpaces the sub length, typically approaching transit. Combine it with `Keyhole = WaitUntilBelow` plus an upstream `WaitUntil` (expression `FieldRotation_MaxSub >= T`, `SequenceItem/Utility/WaitUntil.cs:55-74`) before a second Imaging block to resume west of the meridian. This is the plan's "image east or west of the meridian" advice, built from upstream items.
   - **Clamp**: generate `TakeExposure.ExposureTime` as the expression `Min(T, Floor(FieldRotation_MaxSub))`, using `Min` and `Floor` (`Logic/SymbolFunctions/MathFunctions.cs:100,160`). `ExposureTime` is expression-backed (`TakeExposure.cs:84`) and re-evaluated per exposure. This **breaks "one exposure length per target per night"** (plan decision 7) and dark matching in Siril, so the UI must say so. It is not a default.

A dedicated `FieldRotationCondition` is unnecessary given `LoopWhile` and `WaitUntil`. If wanted later, it would be a `LoopWhile` specialisation that does not throw when the mount is disconnected.

---

## 6. Project layout, ordering, effort, risks

### 6.1 Layout

All new; none of the projects owned by the concurrent workflow are touched.

| Path | AssemblyName | Contents |
|---|---|---|
| `mac/src/NINA.Sequencer.Generators.Mac/` | `NINA.Sequencer.Generators` | `netstandard2.0` analyzer linking `$(NinaRoot)NINA.Sequencer.Generators/*.cs` (§2.3) |
| `mac/src/NINA.WPF.Base.Mac/` | `NINA.WPF.Base` | the 42-file slice of §1.3 (+ the 9 device-VM files and `IImageStatisticsVM` in M7b), `Replaced/IFramingAssistantVM.cs`, `README.md` |
| `mac/src/NINA.Sequencer.Mac/` | `NINA.Sequencer` | links `$(NinaRoot)NINA.Sequencer/**/*.cs` with the Remove lists of §1.5; `Replaced/Editing/{SequenceEditContext,SequencePropertyCapture}.cs`; `Properties/AssemblyInfo.Mac.cs` (no `ThemeInfo`; `InternalsVisibleTo` `NINA.Mac.Sequencing` and `NINA.Mac.Sequencing.Test`); namespace anchors unless P1 lands; `README.md` |
| `mac/src/NINA.Mac.Sequencing/` | `NINA.Mac.Sequencing` | `Catalogue/` (catalogue, `HeadlessSequencerFactory`, icons resource stub); `Headless/` (`DisconnectedDeviceVM<T>`, `HeadlessImageHistory`, `HeadlessApplicationStatus`, `RawRenderedImage`, `HeadlessImageControlVM`, `HeadlessImageStatisticsVM`, null framing/application/planetarium/dome-follower, `HeadlessServices` composition root); `Linked/` (links `NINA/ViewModel/ImagingVM.cs`, `NINA/ViewModel/ImageSaveController.cs`); `Conditions/MaxAltitudeCondition.cs`; `FieldRotation/` (calculator, symbols); `Planning/` (`NightPlan`, `SequenceTreeGenerator`, `PlanValidator`); `Runner/HeadlessSequenceRunner.cs`; `README.md` |
| `mac/tests/NINA.Mac.Sequencing.Test/` | — | NUnit 4.4.0, FluentAssertions [7.0.0], Microsoft.NET.Test.Sdk 18.8.1, NUnit3TestAdapter 6.2.0, Moq 4.20.72 (the upstream test stack); own tests + selected upstream tests linked (§6.4) |

`NINA.Sequencer.Mac.csproj` should import `../Engine.props` once the other workflow finishes it; it already sets `GenerateAssemblyInfo=false`, `UseWPF=false` and links `CommonAssemblyInfo.cs`. Packages: NCalcSync 7.0.0, Parlot 1.5.8, Newtonsoft.Json 13.0.4, System.ComponentModel.Composition 10.0.10 (for `[Export]`/`[ExportMetadata]` attributes, still read by the catalogue), CommunityToolkit.Mvvm 8.4.2 (`[ObservableProperty]` in `TemplateController`). Without P1 it also needs Nito.AsyncEx, Accord(.Math/.Statistics), Google.Protobuf, ASCOM.Com.Components, AsyncEnumerator, OxyPlot.Core, System.Data.SQLite.EF6 and nikoncswrapper, or the namespace anchors.

### 6.2 Ordering and dependencies

```
other workflow (now):  WpfCompat ─┬─ Core.Mac ─┬─ Astrometry.Mac ─┬─ Profile.Mac
                                  │            │                  │
M3 remainder:                     └─ Image.Mac ┴─ Equipment.Mac ───┴─ PlateSolving.Mac
                                                     │
M7:  7.1 Generators.Mac (independent: can start now)│
     7.6a FieldRotationCalculator + tests (independent: can start now)
     7.2 WPF.Base.Mac slice ◄────────────────────────┘
     7.3 Sequencer.Mac (+ WpfCompat additions §1.4)
     7.4 catalogue + factory + JSON tests
     7.5 headless services + sims        (uses M4 driver/sim if ready, else SimMount)
     7.6b MaxAltitudeCondition + FieldRotation symbols
     7.7 NightPlan + generator + validator
     7.8 simulated-night harness → "done when"
```

Hand the WpfCompat owner §1.4 now; it is the only cross-workflow contract. Five of its types must be **the same types** that `NINA.Core.Mac`, `NINA.Image.Mac` and `NINA.Equipment.Mac` compile against: `GeometryGroup`, `BitmapSource`, `MessageBoxButton`/`MessageBoxResult`, `ResizeMode`/`WindowStyle`. Otherwise the artefacts of Appendix A become real errors.

### 6.3 Expected-behaviour notes for implementers

- **Expression-backed properties.** Set and read the generated scalar properties, not `XExpression.Definition`. The generator's analyzers warn otherwise (EXP0100, `NINA.Sequencer.Generators/ARCHITECTURE.md`).
- **`DitherAfterExposures` cadence.** `AfterExposures` counts *image history*, which only `TakeExposure` with type LIGHT or SNAPSHOT feeds (`TakeExposure.cs:175`). A unit test should check "dither every 5" against the headless history.
- **Generated trees must survive a JSON round-trip.** Save each night's tree to the session folder: it serves as both a log and a restart point.

### 6.4 Tests

**Own tests:**
- Catalogue drift guards (§2.2).
- Template, corpus and round-trip loading with Unknown counts (§3).
- Generator shape: a golden structure dump per `NightPlan`.
- `MaxAltitudeCondition`:
  - Check at fixed coordinates and times through a synthetic target whose current altitude is computed;
  - clone, JSON and expression validation;
  - interruption via the watchdog within 6 s.
- Field-rotation table (the 6 plan values ±0.05 s) and symbol publication.
- `DisconnectedDeviceVM` null-safety for `Center` with dome and safety monitor absent.
- `HeadlessImageHistory` counting.

**Simulated night** (the "done when"). Each run takes a few minutes in real time, because of the clock rule in §4.2.5:
1. **Horizon stop.** Synthesise a target that is 0.3° above the custom horizon and setting.
2. **Max-altitude stop.** Synthesise a target near transit, with max set 0.1° above its current altitude.
3. **Dawn stop.** Use a `TimeCondition` whose `TimeProvider` fires at now + 3 min. Separately, a unit test checks `DawnProvider`'s time for a known date at 22.25 N 114.18 E against `NighttimeCalculator`.

For each run, assert:
- frames are named and placed per the Siril layout (M3/M9 `ImageFileSettings`) with the right FITS `OBJECT` and `IMAGETYP`;
- dither pulses reached `SimMount` every k frames;
- the Imaging block ended within one watchdog period of the threshold.

**Linked upstream tests** (`NINA.Test/Sequencer/**`, same NUnit, FluentAssertions and Moq stack). Candidates:
- `Conditions/{AltitudeCondition,AboveHorizonCondition,LoopCondition,LoopWhile,TimeCondition,TimeSpanCondition,SunAltitudeCondition}Test.cs`
- `SequenceItem/Imaging/{TakeExposure,TakeManyExposures,SmartExposure}Test.cs`
- `SequenceItem/Platesolving/{Center,SolveAndSync}Test.cs`
- `Trigger/Guider/DitherAfterExposuresTest.cs`, `Trigger/Platesolving/{CenterAfterDriftTrigger,PlatesolvingImageFollower}Test.cs`
- `Serialization/*`, `SequencerTest.cs`

25 of the 161 upstream sequencer test files reference STA, `Application.Current` or WPF controls; exclude them. This gives behavioural parity with Windows NINA at low cost.

### 6.5 Effort

Full-time-equivalent days with an AI assistant.

| Item | Days |
|---|---|
| 7.1 Generators.Mac | 0.5 |
| 7.2 WPF.Base.Mac slice (42 files, reduced `IFramingAssistantVM`) | 1–1.5 |
| 7.3 Sequencer.Mac (188 files, 2 trimmed copies, AssemblyInfo) + WpfCompat additions + first green build | 1.5–2 |
| 7.4 Catalogue, `HeadlessSequencerFactory`, drift guards, template/corpus/round-trip tests | 1.5–2 |
| 7.5 Headless services (disconnected handlers, history, status, `ImagingVM`/`ImageSaveController` links, raw rendered image, composition root) + `SimCamera`/`SimMount`/`SimPlateSolverFactory` | 2.5–4 |
| 7.6 `MaxAltitudeCondition` + field-rotation calculator/symbols + tests | 1–1.5 |
| 7.7 `NightPlan`, generator, validator + tests | 2–3 |
| 7.8 Simulated-night harness, runs, fixes | 2–3 |
| **Total** | **12–17.5 days (≈ 2.5–3.5 weeks)** |

The plan says 1.5–2.5 weeks. The extra time is 7.5 and 7.8, which make M7 testable headless and later become M8's service layer. If M8 builds that layer first, M7 shrinks by about 3–5 days.

### 6.6 Risks

1. **Prerequisite projects missing.** `NINA.Equipment.Mac`, `NINA.Image.Mac` and `NINA.PlateSolving.Mac` don't exist yet, and Equipment has the most Windows code. M7 can't build until they compile. Steps 7.1 and 7.6a can start now.
2. **Compat type identity and semantics.**
   - The 5 shared types of §6.2 must be one assembly's.
   - `WeakEventManager` must work.
   - `Application.Current` should be non-null headless, with an inline or queued `Dispatcher`, because `TemplateController.cs:186,211` and `TargetController.cs:193,231` dereference it unguarded; or keep those controllers uninstantiated (the M7 design does).
   - The `AsyncObservableCollection` dispatcher choice (`NINA.Core/Utility/AsyncObservableCollection.cs:29-30`) affects every collection the sequencer touches.
3. **Hard-coded factories in `CenterAfterDriftTrigger`** (`:141-142`) and `PlatesolvingImageFollower` (`:136-137`). There is no injection seam for simulated solves, and a WPF `WindowServiceFactory` would crash headless. Mitigation: headless-safe `WindowServiceFactory` in `NINA.Core.Mac`, M6's solver with a fake `astap` for tests, upstream patch P2.
4. **Editing churn upstream.** The two trimmed copies drift; the hash test (§2.5) catches it. P4 removes the copies.
5. **Real-time clocks.** Simulations take minutes and thresholds are relative to *now*. A failing sim is slow to iterate, so keep most logic in unit tests with synthetic coordinates.
6. **Image preparation.** `CaptureAndPrepareImage` (`NINA.Platesolving/CaptureSolver.cs:55`) and `ImagingVM.PrepareImage` need an `IRenderedImage`. Until M5, the raw-only adapter returns no bitmap or thumbnail and no HFR. `PlateSolvingStatusVM.Thumbnail` stays null, which is acceptable headless.
7. **Expressions.**
   - Unqualified `Altitude` is ambiguous between Mount and Dome; always use `Mount_Altitude`.
   - `LoopWhile` throws on uninitialised symbols, so a disconnected mount fails a container that uses `LoopWhile`. Prefer `MaxAltitudeCondition` and `FieldRotation_*` symbols, which always publish.
8. **Portability.** Sequences containing `MaxAltitudeCondition` load in Windows NINA with an Unknown condition; offer "export for Windows" (§3).
9. **Mount behaviour that only the M2/M4 hardware tests settle:** pulse dithering in alt-az, soft park, `:So`. M7 uses simulators, so passing M7 says nothing about the real mount.

### 6.7 Proposed upstream patches

All are Windows-neutral and small enough to offer upstream, citing #144 and disclosing AI assistance per `CONTRIBUTING.md`. **None was applied.**

| # | Change | Why | Verified |
|---|---|---|---|
| P1 | Delete unused `using`s (list below) | Headless or partial builds then need no ASCOM, Accord, Protobuf, Dasync, Nikon or EF references, and the namespace anchors go away | Yes: rig probe compiled with these lines removed and those packages absent (Appendix A) |
| P2 | `CenterAfterDriftTrigger`: add `IPlateSolverFactory` and `IWindowServiceFactory` to the `[ImportingConstructor]` (PluginLoader already composes both, `PluginLoader.cs:592-593`) and use them at `:141-142`; pass the factory into `PlatesolvingImageFollower` (its `internal` ctor) instead of the static `PlateSolverFactory` (`:136-137`) | Testability; consistency with `Center`/`ProgrammableMeridianFlipTrigger`, which already take both | No (design only) |
| P3 | `TemplateController.cs:186,211` and `TargetController.cs:193,231`: use `Application.Current?.Dispatcher` with an inline fallback, as `SymbolController.cs:86-93` already does | Null-safety when no WPF app is running (e.g. unit tests, headless hosts) | No |
| P4 | Split `Editing/SequenceEditContext.cs` and `Editing/SequencePropertyCapture.cs` into a WPF-free core and `*.Wpf.cs` partials (attached properties, `StepExpression`, `HistoryCommand`, `Create(…, BindingExpression)`) | Downstream headless builds exclude files instead of copying them | Partly: the probe's trimmed copies compiled |
| P5 | Make `Sequencer.Validate` (`Sequencer.cs:178`) public, e.g. `public IList<string> Validate()` on `ISequencer` | Lets hosts show issues without `MyMessageBox` | No |

P1 deletes 16 lines in 13 files:
- `Sequencer.cs:16,20,23`
- `Trigger/Guider/DitherAfterExposures.cs:15` and `Trigger/Platesolving/CenterAfterDriftTrigger.cs:15` (`ASCOM.Com.DriverAccess`)
- `SequenceItem/Utility/WaitForSunAltitude.cs:30-31` (`Nito.AsyncEx`, `Nikon`)
- `WaitForMoonAltitude.cs:30`, `Conditions/MoonAltitudeCondition.cs:24`, `Conditions/SunAltitudeCondition.cs:23` (`Nito.AsyncEx`)
- `SequenceItem/FilterWheel/SwitchFilter.cs:15-17` (`Accord.IO`, `Accord.Statistics…`, `Google.Protobuf.WellKnownTypes`)
- `SequenceItem/Telescope/CoordinatesInstruction.cs:1` (`Google.Protobuf.WellKnownTypes`)
- `SequenceItem/Utility/WaitForTimeSpan.cs:15` and `SymbolController.cs:1` (`Accord.Math`)
- `Container/SequenceContainer.cs:15` (`Dasync.Collections`)

`TargetController.cs:33` `using OxyPlot.Axes;` is **used** (`DateTimeAxis` at `:287`) and must stay.

---

## Appendix A: probe method and exact results

All throwaway, under `/private/tmp/m7probe`. Build tool: `"/Users/williamliang/N.I.N.A port/nina/mac/dotnet"` (SDK 10.0.401).

1. **`up/`**: `rsync` copy of the upstream dependency projects. Command and result:

   ```
   dotnet build NINA.Sequencer/NINA.Sequencer.csproj -c Debug -p:EnableWindowsTargeting=true -p:GeneratePackageOnBuild=false
   Build succeeded.  1533 Warning(s)  0 Error(s)  Time Elapsed 00:00:35.74
   ```

   It produced `net10.0-windows` DLLs for Core, Astrometry, Profile, Equipment, Image, PlateSolving, WPF.Base, CustomControlLibrary and Sequencer, plus the `netstandard2.0` generator.
2. **`compat/ProbeCompat.csproj`**: inert stand-ins in WPF namespaces. These are **not** a proposal for `NINA.Mac.WpfCompat`; they only open the next error layer.
3. **`seqprobe/SeqProbe.csproj`**: `net10.0`, `AssemblyName=NINA.Sequencer`. It links upstream sources, references the generator DLL as analyzer and assembly, references the Windows-built dependency DLLs, and selects a file set with `-p:ProbeSet=all|rig|full`. File counts from `dotnet msbuild -getItem:Compile`:

   | Probe set | Compile items | Upstream files |
   |---|---|---|
   | all | 235 | 234 |
   | rig | 192 | 188 |
   | full | 231 | 228 |

   The `rig` set added `mac/Editing/SequenceEditContext.Headless.cs` (105 lines), `mac/Editing/SequencePropertyCapture.Headless.cs` (84 lines) and `mac/NamespaceAnchors.cs`.
4. **Final rig build: 5 unique errors**, all `CS0012` "type … defined in an assembly that is not referenced … PresentationFramework":
   - `Container/SequenceRootContainer.cs:58,66` (`MessageBoxResult`)
   - `Sequencer.cs:115` (`MessageBoxResult`)
   - `SequenceItem/Platesolving/Center.cs:169` and `SolveAndSync.cs:98` (`ResizeMode`)

   Adding the real WPF reference assemblies under an `extern alias` turned these into `CS1503`/`CS0019` conversions between the stand-in enums and the PresentationFramework enums at the same 5 sites. That shows they are type-identity artefacts of mixing a Windows-built `NINA.Core.dll` with stand-ins.
5. **`usings/`**: the rig set with the P1 lines deleted and without the Accord, ASCOM, Google.Protobuf, Dasync, EF6 and Nikon references. The first build also showed `TargetController.cs:287` `DateTimeAxis` (CS0103), proving `using OxyPlot.Axes;` is needed. After restoring that line and adding the transitive Nito.AsyncEx and OxyPlot.Core packages (needed through Image's `AsyncLazy<>` and Core's OxyPlot types), the same 5 artefacts remained and nothing else.
6. **`wpfbase/WpfBaseSlice.csproj`** (`AssemblyName=NINA.WPF.Base`): the 42-file slice plus the reduced `IFramingAssistantVM`. Throwaway copies of `DockableVM.cs` and `ImagingMediator.cs` were patched with `extern alias` to cancel the identity artefacts. With `-p:WithVMs=true` it adds `CameraVM`, `TelescopeVM` (+`TelescopeSlewCoordinates`, `TelescopeLatLongSyncVM`), `GuiderVM` and `FocuserVM` (+3 decorators). Remaining errors were only `CS0029`/`CS1503` conversions between stand-in and real `GeometryGroup`, `BitmapSource`, `MessageBox*`, `ResizeMode` and `WindowStyle`. Removing any one of the 6 tested packages (Nito.AsyncEx, OxyPlot.Core, System.ComponentModel.Composition, Newtonsoft.Json, CsvHelper, AsyncEnumerator) added 1–74 errors.
7. **`seqprobe/mac/Rig/MaxAltitudeCondition.cs`** compiled in the rig set through the upstream generator. The generated `Clone()`, `Validate()`, `ValidateAdditional` and `Proxy = "Data.Offset"` all resolved; the first attempt failed only on the base-class namespace.
8. **`frcheck/`**: `dotnet run` of the `FieldRotationCalculator` prototype, output verbatim:

   ```
   dec   -30 HA 0 alt  37.8  maxSub  10.6s  plan 10.6s  MATCH
   dec   -30 HA 3 alt  22.2  maxSub  16.6s  plan 16.6s  MATCH
   dec     0 HA 0 alt  67.8  maxSub   5.1s  plan 5.1s  MATCH
   dec     0 HA 3 alt  40.9  maxSub  28.7s  plan 28.7s  MATCH
   dec    10 HA 0 alt  77.8  maxSub   2.9s  plan 2.9s  MATCH
   dec    10 HA 3 alt  45.3  maxSub  64.7s  plan 64.7s  MATCH
   ```

**Not done:**
- Nothing ran inside the sequencer engine (no deserialisation and no `Sequencer.Start`): the probe assemblies can't load on macOS against Windows-built dependencies.
- No upstream test was run.
- The merged sequencer + slice compile reached only declaration-level type-identity errors, so its method bodies were not verified.
- The Editing binding stand-in growth was observed for one round only (§1.6, round 6) before switching to the trimmed copies.

## Appendix B: the rig compile set by folder (188 upstream files)

| Folder | Files | | Folder | Files |
|---|---|---|---|---|
| (root): `Sequencer`, `SequencerFactory`, `SequenceEntityINPC`, `DropInParameters`, `SymbolController`, `SymbolFunctionController`, `TargetController`, `TemplateController`, `TemplateLinkResolver` | 9 | | `Serialization/` | 8 |
| `Conditions/` | 14 | | `Container/` + `ExecutionStrategy/` | 13 + 3 |
| `Editing/`, upstream files kept (+2 trimmed copies) | 22 | | `Interfaces/` (+ `DragDrop` 2, `Mediator` 1, `ViewModel` 3) | 22 + 6 |
| `Logic/` + `SymbolFunctions/` | 14 + 5 | | `Mediator/` | 1 |
| `SequenceItem/` (base + Unknown) | 2 | | `SequenceItem/Camera`, `Connect`, `Expressions` | 5, 4, 7 |
| `SequenceItem/FilterWheel`, `FlatDevice`, `Guider` | 1, 5, 1 | | `SequenceItem/Imaging`, `Platesolving`, `Telescope`, `Utility` | 3, 2, 6, 11 |
| `Trigger/` (base + Unknown), `Connect`, `Guider`, `Platesolving` | 2, 2, 1, 2 | | `Utility/` + `DateTimeProvider/` | 6 + 11 |

The excluded files are listed in §1.5: 41 item files and 5 editor files. The 25 `View/` and `Behaviors/` `.cs` files and all `.xaml`/`.xaml.cs` are also excluded.
