# psd-mcp: PSD → Unity uGUI with Claude Code

[Tiếng Việt](README.md) · **English**

Give Claude your artist's PSD and it builds the uGUI to match the design: positions, sizes, sprites, fonts and text colors, then renders the result so you can compare it with the PSD. It covers three cases:

- **Update an existing prefab** from a new PSD (keeps scripts, anchors, Animator).
- **Build a new prefab** when the UI does not exist yet: creates objects, sprites, text and inferred anchors.
- **UI placed directly in a scene**: build or update it, with Undo; you save the scene yourself.

You don't need to remember any tool names. After installing, just say:

> Build the Victory screen from `D:/design/victory.psd` (image `victory.jpg`) into the prefab `Assets/UI/Victory.prefab`.

> Make a new UI from `shop.psd` and save it as `Assets/UI/Shop.prefab`.

> Build `shop.psd` into the Canvas of the `Lobby` scene.

Claude reads the PSD and shows you the list of changes (or the object tree it will create) first. Nothing is saved until you agree.

## What's inside

| Part | Where | Does |
| --- | --- | --- |
| MCP server `psd2ui` | `psd_mcp/` (Python) | Reads the PSD, exports sprites, builds the layout spec, compares images |
| Skill `psd-to-ugui` | `skills/` | Teaches Claude the workflow and where it must ask you |
| Unity package | `unity/` | 4 Editor tools: build new UI, match nodes, apply spec, render (prefab or scene) |

## Requirements

- [Claude Code](https://claude.com/claude-code)
- [uv](https://docs.astral.sh/uv/) (runs the Python server and installs its libraries on first run)
- Unity 2022.3 or later with [unityMCP](https://github.com/CoplayDev/unity-mcp) (`com.coplaydev.unity-mcp`) connected to Claude Code. Without unityMCP you can still read and export PSDs.

## Install

**1. Claude Code plugin** (MCP server + skill):

```bash
claude plugin marketplace add buinghiemtrang/psd-mcp
claude plugin install psd-mcp@psd-mcp-marketplace
```

Restart Claude Code and type `/mcp`. If you see the `psd2ui` server, you're done.

**2. Unity package**: Package Manager → `+` → Add package from git URL:

```
https://github.com/buinghiemtrang/psd-mcp.git?path=/unity
```

That's it. No config is needed: fonts are picked by the font name in the PSD (e.g. `UTM-Eremitage` → `UTM EREMITAGE SDF`), and Claude asks where to put new sprites.

**Optional:** to force a specific font / material or fix the sprite folder, import the "Config mẫu" sample (Package Manager → PSD Layout → Samples), place `PsdLayoutConfig.json` anywhere under `Assets/`, and edit `fonts`, `sprite_dir`, `import_like`.

## Usage

Talk to Claude in plain words. For example:

| You say | Claude does |
| --- | --- |
| "What's in `shop.psd`?" | Lists layers, fonts, text sizes, colors, hidden layers |
| "Export the buttons in `shop.psd` to PNG" | Alpha-trimmed PNGs + coordinates |
| "Apply `shop.psd` to `Assets/UI/Shop.prefab`" | Updates the existing prefab to match the PSD |
| "Make a new UI from `shop.psd`, save as `Assets/UI/Shop.prefab`" | Builds a new prefab: objects, sprites, text, anchors |
| "Build `shop.psd` into the Canvas of the `Lobby` scene" | Builds it in the scene (with Undo, you save) |
| "Compare the Shop prefab with the PSD" | Side-by-side image: PSD, Unity, differences |

The workflow (Claude runs it; you only answer when asked):

1. Read the PSD and find hidden layers (disabled states, old versions).
2. Build the layout spec: each layer becomes an image, text (TMP) or button; gradient text colors come from the JPG.
3. Find existing sprites that look the same and reuse them, so no duplicate sprites are added.
4. Existing UI: match each node to an object by position, name, text and sprite. New UI: name objects by their role and infer anchors.
5. **Dry run**: list the changes / the tree to create. You review and agree.
6. Save the prefab (keeping anchors, pivots, scripts, Animator), or change the scene for you to review and save.
7. Render, compare with the PSD, adjust if anything is off.

## Limitations

- psd-tools cannot render GradientOverlay / InnerGlow on text. Include the JPG exported by the artist to get the right colors.
- Anchors of newly built UI are inferred (top / middle / bottom by position); check them on unusual screen sizes. You still have to assign script fields for new objects.
- Coordinates follow the PSD canvas (e.g. 1080×1920), which should match the CanvasScaler Reference Resolution.

## Tools (reference)

| Tool | Side | Does |
| --- | --- | --- |
| `psd_tree` | psd2ui | Layer tree: id, kind, real rect (alpha-trimmed), hidden, fx, font / size / text color |
| `psd_states` | psd2ui | Pairs hidden layers with visible layers at the same place |
| `psd_export` | psd2ui | Exports layers to alpha-trimmed PNG + rect |
| `psd_render` | psd2ui | Composite image of the whole screen / some layers |
| `psd_compare` | psd2ui | Side-by-side PSD, Unity and difference image |
| `psd_to_spec` | psd2ui | Builds `spec.json` + PNGs |
| `psd_match_sprites` | psd2ui | Finds existing sprites that match the exported PNGs |
| `psd_text_tint` | psd2ui | Text color / gradient sampled from the JPG |
| `psd_split_text` | psd2ui | Splits a merged image (icon + text) |
| `psd_variant` | psd2ui | Generates a grey / dark variant for disabled buttons |
| `build_layout_spec` | Unity | Builds new UI: new prefab or in a scene (dry run by default) |
| `match_layout_spec` | Unity | Matches spec nodes to existing objects (prefab / scene) |
| `apply_layout_spec` | Unity | Applies the spec to existing UI (dry run by default) |
| `render_prefab` | Unity | Renders a prefab / scene object to PNG |

## Development

```bash
uv sync
claude mcp add -s user psd2ui -- uv --directory "$PWD" run psd-mcp   # run the local copy without the plugin
claude plugin validate .
```

MIT License.
