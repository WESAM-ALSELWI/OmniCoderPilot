# OmniCoderPilot: ASP.NET Core → WPF Conversion Plan

## Overview
Convert the OmniCoderPilot AI coding assistant from an ASP.NET Core web application to a WPF desktop application. All 20 agent tools, the autonomous orchestrator, database, and Ollama integration are preserved. Only the UI layer changes.

## Architecture Change

```
BEFORE (ASP.NET Core):
  Browser (HTML/JS/CSS) → SignalR Hub → AgentOrchestrator → Tools → Ollama
                    ↕ REST API
  Controllers → EF Core/SQLite

AFTER (WPF):
  WPF UI (XAML/C#) → EventAggregator → AgentOrchestrator → Tools → Ollama
                    ↕ direct service calls
  ViewModels → EF Core/SQLite
```

---

## Phase 1: Project Restructuring
**Goal**: Create the WPF project shell and move backend code.

### 1.1 Create New WPF Project
- Create `OmniCoderPilot.slnx` solution with two projects:
  - `OmniCoderPilot.Core` (class library, `net10.0`) — all backend code
  - `OmniCoderPilot.Wpf` (WPF app, `net10.0-windows`) — UI layer
- `OmniCoderPilot.Wpf` references `OmniCoderPilot.Core`

### 1.2 Move Backend to Core Library
Move these files/directories from `OmniCoderPilot/` to `OmniCoderPilot.Core/`:
- `Domain/` — Entities.cs, Enums.cs
- `Application/` — Abstractions.cs, AgentOrchestrator.cs
- `Application/Tools/` — CoreTools.cs, WebTools.cs, TodoTools.cs, PlanModeTools.cs
- `Infrastructure/` — All 10 service implementations
- `appsettings.json` — configuration

### 1.3 Update .csproj Files

**OmniCoderPilot.Core.csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="6.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="6.0.0" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.FileSystemGlobbing" Version="8.0.0" />
    <PackageReference Include="HtmlAgilityPack" Version="1.11.71" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" />
  </ItemGroup>
</Project>
```

**OmniCoderPilot.Wpf.csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\OmniCoderPilot.Core\OmniCoderPilot.Core.csproj" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
  </ItemGroup>
</Project>
```

### 1.4 Files to DELETE (ASP.NET Core specific)
- `OmniCoderPilot/Program.cs` — replaced by App.xaml.cs
- `OmniCoderPilot/Controllers/` — HomeController.cs, WorkspaceController.cs
- `OmniCoderPilot/Web/AgentHub.cs` — replaced by EventAggregator
- `OmniCoderPilot/Models/ErrorViewModel.cs` — not needed
- `OmniCoderPilot/Views/` — all .cshtml files
- `OmniCoderPilot/wwwroot/` — all JS, CSS, HTML, lib files
- `OmniCoderPilot/Properties/launchSettings.json` — WPF doesn't use this

---

## Phase 2: WPF Infrastructure
**Goal**: Set up the WPF application shell, DI, theming, and event system.

### 2.1 App.xaml.cs — DI Container
```
- Build IServiceProvider with all Core services
- Register all 20 agent tools
- Register WPF-specific services (INavigationService, IEventAggregator)
- Set DataContext of MainWindow to MainViewModel
```

### 2.2 Event Aggregator (replaces SignalR)
Create `EventAggregator` class implementing `IEventAggregator`:
- `Publish<T>(T event)` / `Subscribe<T>(Action<T> handler)`
- Thread-safe (events from orchestrator background thread must marshal to UI thread)
- Events to support:
  - `TokenReceivedEvent` — streaming response tokens
  - `ThinkingStepEvent` — thinking/reasoning step
  - `ToolStepEvent` — individual tool step
  - `ToolStepsBatchEvent` — grouped tool steps
  - `AgentActivityEvent` — live activity banner
  - `StatusEvent` — progress updates
  - `ToolEvent` — tool card updates
  - `DiffEvent` — diff available
  - `ErrorEvent` — error messages
  - `TodoEvent` — task list updates
  - `PermissionRequestEvent` — permission approval needed

### 2.3 Theme System
Create `Themes/DarkTheme.xaml` and `Themes/LightTheme.xaml` ResourceDictionaries:
- All color tokens as `SolidColorBrush` and `LinearGradientBrush` resources
- Toggle via `Application.Current.Resources.MergedDictionaries`
- Persist selection in settings

### 2.4 Navigation Service
Simple `INavigationService` that swaps the main content area's DataContext.

---

## Phase 3: WPF UI — Main Window Layout
**Goal**: Three-column layout matching the web UI.

### 3.1 MainWindow.xaml Structure
```xml
<Grid>
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="280"/>  <!-- Left Sidebar -->
    <ColumnDefinition Width="*"/>    <!-- Chat Main -->
    <ColumnDefinition Width="340"/>  <!-- Right Rail -->
  </Grid.ColumnDefinitions>

  <!-- Left Sidebar -->
  <DockPanel Grid.Column="0">
    <!-- Brand, New Chat, Workspace Picker, Model Selector, Conversation List -->
  </DockPanel>

  <!-- Chat Main -->
  <DockPanel Grid.Column="1">
    <!-- Chat Header, Activity Banner, Message List, Composer -->
  </DockPanel>

  <!-- Right Rail -->
  <Grid Grid.Column="2">
    <Grid.RowDefinitions>
      <RowDefinition Height="1*"/>  <!-- Task Plan -->
      <RowDefinition Height="1*"/>  <!-- Activity Log -->
      <RowDefinition Height="1.4*"/> <!-- Tool Cards -->
      <RowDefinition Height="1.6*"/> <!-- File Tree -->
    </Grid.RowDefinitions>
  </Grid>
</Grid>
```

### 3.2 ViewModels
- `MainViewModel` — orchestrates all child ViewModels
- `SidebarViewModel` — workspace, model, conversations
- `ChatViewModel` — messages, composer, streaming state
- `ActivityViewModel` — activity log entries
- `ToolCardsViewModel` — tool execution cards
- `TodoViewModel` — task list with progress
- `FileTreeViewModel` — workspace file tree
- `DiffViewModel` — change set diffs

---

## Phase 4: WPF UI — Chat Interface
**Goal**: Full chat with streaming, thinking steps, tool steps.

### 4.1 Message List
- `ItemsControl` with `ObservableCollection<ChatMessageViewModel>`
- User messages: right-aligned blue-tinted border
- Assistant messages: left-aligned with robot icon, contains:
  - `ItemsControl` of `ThinkingStepViewModel` (Expander controls)
  - `ItemsControl` of `ToolStepGroupViewModel` (nested Expanders)
  - `RichTextBox` or `TextBlock` with FlowDocument for markdown

### 4.2 Streaming Assembly
- `TokenReceivedEvent` appends to `AssistantBuffer`
- Markdown rendered incrementally (simple regex-based, no external lib)
- Blinking cursor `▊` via `TextBlock` with `Opacity` animation while `IsStreaming`

### 4.3 Thinking Steps
- Each is a WPF `Expander` with custom `HeaderTemplate`:
  - Chevron icon (rotates on expand)
  - "Thought for Xs" label
- Content: `TextBlock` with muted text, max height 400, scrollable

### 4.4 Tool Step Lines
- Each is a `Border` with colored `BorderBrush` (left side only):
  - Edit/Write: Green `#34d399`
  - Read/Explore: Blue `#60a5fa`
  - Search: Purple `#a78bfa`
  - Command: Yellow `#fbbf24`
  - Delete: Red `#f87171`
- Inner `StackPanel` (horizontal): Label + LangBadge + FilePath + Detail

### 4.5 Grouped Tool Steps
- Outer `Expander` for "Explored N files"
- Inner `ItemsControl` of sub-entries (indented `Border` with left line)

### 4.6 Composer
- `TextBox` with `TextWrapping="Wrap"`, `AcceptsReturn="True"`, max height 200
- `Button` with gradient `LinearGradientBrush` and arrow icon
- Enter keybinding to send (without Shift)

---

## Phase 5: WPF UI — Supporting Panels
**Goal**: All supporting UI from the web version.

### 5.1 Sidebar Components
- **Brand**: Gradient icon + title (styled `TextBlock` with `LinearGradientBrush`)
- **New Chat Button**: Full-width gradient `Button`
- **Workspace Picker**: `TextBox` + `Button` (Browse) + `Button` (Open)
- **Model Selector**: `ComboBox` + refresh `Button` + status indicator `Ellipse`
- **Conversation List**: `ItemsControl` with `ListBoxItem`-style items

### 5.2 Right Rail Panels
- **Task Plan**: `ProgressBar` (gradient fill) + `ItemsControl` of todo items with status icons
- **Activity Log**: `ItemsControl` with timestamped entries, colored left borders
- **Tool Cards**: `ItemsControl` with expandable cards, status pills, output preview
- **File Tree**: `TreeView` with `HierarchicalDataTemplate` for folders/files, click-to-preview

### 5.3 Dialogs (WPF Windows)
- **Permission Approval**: Modal `Window` with tool name + args + Allow/Deny buttons
- **Folder Browser**: Modal `Window` with breadcrumb + directory listing + Open button
- **Workspace Chooser**: Modal `Window` with workspace cards

### 5.4 Toast Notifications
- Custom `UserControl` with `Popup` or `AdornerLayer`
- Auto-dismiss after 3 seconds
- Fade-in/out animations

---

## Phase 6: Integration & Wiring
**Goal**: Connect WPF UI to backend services.

### 6.1 Replace SignalR Hub Calls
- `SendPrompt` → directly call `IAgentOrchestrator.RunTurnAsync()` on background thread
- `ApplyChangeSet` → directly call `IDiffService.ApplyChangeSetAsync()`
- `RespondToPermission` → directly call `PermissionGateway.RespondAsync()`
- `GetModels` → directly call `IOllamaClient.ListModelsAsync()`

### 6.2 Replace REST API Calls
- Workspace operations → directly call `IWorkspaceFileService` and `AppDbContext`
- File tree → directly call `WorkspaceFileService.GetTreeAsync()`
- File read → directly call `WorkspaceFileService.ReadFileAsync()`
- Conversations → directly call `AppDbContext`
- Todos → directly call `ITodoService`

### 6.3 Wire EventAggregator
- `AgentOrchestrator` publishes events via `IAgentEventSink`
- `SignalRAgentEventSink` replaced by `WpfEventSink` that publishes to `EventAggregator`
- Each ViewModel subscribes to relevant events
- UI updates marshaled to dispatcher thread

---

## Phase 7: Polish & Features
**Goal**: Ensure feature parity with web version.

### 7.1 Markdown Rendering
- Implement simple markdown-to-FlowDocument converter:
  - Code blocks with language header + copy button
  - Bold, italic, headers
  - Lists (ordered/unordered)
  - Inline code
  - Horizontal rules

### 7.2 Diff View
- Custom `ItemsControl` rendering unified diff
- Green lines for additions, red for deletions
- "Apply" button per change set

### 7.3 Keyboard Shortcuts
- Enter: Send prompt
- Shift+Enter: Newline in prompt
- Ctrl+N: New chat
- Ctrl+Shift+O: Open workspace

### 7.4 Theme Persistence
- Save theme selection to `settings.json` or `Properties.Settings`

### 7.5 Window State Persistence
- Save window size/position on close, restore on open

---

## File Inventory (After Conversion)

### OmniCoderPilot.Core/ (Class Library)
```
Domain/
  Entities.cs
  Enums.cs
Application/
  Abstractions.cs
  AgentOrchestrator.cs
  Tools/
    CoreTools.cs
    WebTools.cs
    TodoTools.cs
    PlanModeTools.cs
Infrastructure/
  AppDbContext.cs
  OllamaClient.cs
  TerminalService.cs
  WorkspaceFileService.cs
  DiffService.cs
  AgentContextServices.cs
  ProjectMemoryService.cs
  RepositoryIndexer.cs
  WebServices.cs
  TodoService.cs
  SqliteSchemaInitializer.cs
appsettings.json
```

### OmniCoderPilot.Wpf/ (WPF Application)
```
App.xaml / App.xaml.cs
Themes/
  DarkTheme.xaml
  LightTheme.xaml
ViewModels/
  MainViewModel.cs
  SidebarViewModel.cs
  ChatViewModel.cs
  MessageViewModel.cs
  ThinkingStepViewModel.cs
  ToolStepViewModel.cs
  ActivityViewModel.cs
  ToolCardsViewModel.cs
  TodoViewModel.cs
  FileTreeViewModel.cs
  DiffViewModel.cs
  PermissionViewModel.cs
  FolderBrowserViewModel.cs
  WorkspaceChooserViewModel.cs
Services/
  EventAggregator.cs
  WpfEventSink.cs
  NavigationService.cs
Converters/
  BoolToVisibilityConverter.cs
  ColorToBrushConverter.cs
  StatusToIconConverter.cs
Views/
  MainWindow.xaml / MainWindow.xaml.cs
  ChatMessageView.xaml
  ThinkingStepView.xaml
  ToolStepGroupView.xaml
  ToolCardView.xaml
  DiffView.xaml
  TodoItemView.xaml
  FileTreeView.xaml
  PermissionDialog.xaml
  FolderBrowserDialog.xaml
  WorkspaceChooserDialog.xaml
  ToastNotification.xaml
Resources/
  Icons.xaml (SVG path data for all icons)
```

---

## Estimated Scope
| Component | Current (Web) | New (WPF) |
|-----------|---------------|-----------|
| Backend C# | 3,676 lines | 3,676 lines (unchanged) |
| Frontend JS | 1,268 lines | 0 (removed) |
| HTML Templates | ~178 lines | 0 (removed) |
| CSS | ~1,799 lines | 0 (removed) |
| XAML + C# UI | 0 | ~2,500-3,500 lines (estimated) |
| **Total** | **6,921 lines** | **~6,200-7,200 lines** |

---

## Key Risks
1. **Markdown rendering** — WPF has no built-in markdown support; need custom implementation
2. **Streaming text** — Token-by-token assembly requires careful dispatcher marshaling
3. **Theme system** — WPF resource dictionaries are more verbose than CSS variables
4. **File tree** — `HierarchicalDataTemplate` with lazy loading is complex
5. **Diff rendering** — Color-coded lines need custom control
6. **Modal dialogs** — WPF Window management is more complex than HTML modals
