# Design verification

final result: passed

Scope: restyle the existing WPF recorder toward the supplied reference; preserve working settings and capture bindings. This is a native app, so browser prototype and hosting steps do not apply.

## Evidence
- Source: C:/Users/Ryota/AppData/Local/Temp/codex-clipboard-eb49ddd8-0bb4-4259-a083-ad55fa9bb3e5.png (1219 x 1290).
- Implementation: .artifacts/design-check/capture.png (976 x 994 WPF device-independent units at 96 dpi).
- Combined comparison: .artifacts/design-check/comparison.png. Source title bar cropped at y=48 and remaining content normalized to 976 x 994; implementation uses the app content region. Native title chrome excluded.
- Additional states: compact.png (804 x 681), quality.png, save.png, region.png in the same folder.
- State: window capture settings open, no selected capture target. The reference contains a selected ChatGPT window, so live image, availability, values and disabled recording button differ intentionally. No substitute screenshot is injected into the application.
- Full-view comparison reviewed side by side. Text, tiles, inputs, checkbox states and footer also reviewed in full-resolution individual captures; no further focused crops needed.

## Comparison history
- P2: initial standard-size capture pushed the save card partly below the footer. Reduced header, tile and section spacing; latest capture shows all three cards and the fixed footer without scrolling.
- P2: native light scrollbar and rounded navigation underline diverged from the reference. Added a dark scrollbar template and straight step underline; revised compact and capture images checked.

## Fidelity surfaces
- Typography: Yu Gothic UI / Segoe UI, strong app title and section headings, subdued labels. No clipped labels in checked layouts.
- Layout: two columns, three capture tiles, large preview, collapsed quality/save cards, fixed action bar. Compact content scrolls while recording actions remain visible.
- Colors: charcoal panels, muted gray borders and labels, mint selection and primary actions.
- Images/icons: real preview binding retained; Windows Fluent icon font used for controls. Empty preview is truthful. No generated raster assets are needed.
- Copy: reference heading and action labels retained. Audio summary reflects supported recording modes instead of inventing microphone or resolution controls. Editable window dimensions and reload action retained.

## Validation
- App build succeeded with zero warnings/errors.
- Compiled WPF rendering at standard and minimum viewport sizes succeeded.
- Three step buttons open the intended expander and close the previous one.
- Mode tile selection updates window/region mode in the view model.
- Cursor checkbox updates its view-model setting.
- Changed source is UTF-8 without BOM and CRLF.

## Limits and follow-up polish
- P3: native title-bar appearance and live recording were not exercised by the offscreen WPF render harness.
- Live capture content and saved settings vary with the user's environment. Existing recording pipeline was not modified.
- Minor icon contours and flat panel fills differ from the reference rendering.

## Implementation checklist
- [x] Restyle existing controls and preserve commands/bindings.
- [x] Resolve standard-window overflow and verify compact layout.
- [x] Rebuild and verify primary settings interactions.
- [x] Preserve source encoding and CRLF.

## Requested follow-up: audio target grouping
- User-directed departure from the original reference: recording method and audio source now live below the preview inside the capture card. The card retains its previous height at 976 x 994.
- Step 2 is now labeled コーデック設定 in navigation and the expander. Video codec/frame rate and audio codec/sample rate occupy equal columns.
- Updated capture.png and quality.png reviewed. Existing reference comparison.png documents the earlier design, not this requested follow-up.
- WPF build/render and navigation checks passed. Selecting no audio disables both the audio source and codec controls; selecting system audio enables them again.
- Source remains UTF-8 without BOM, with CRLF. Recording pipeline unchanged.
- Follow-up result: passed.
