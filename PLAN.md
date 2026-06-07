# Plans

## Completed Tasks

### Enter Key Accepts Code Completion
- **Status: Completed**
- **File:** `Beast/Display/DisplayScreen.cs` (lines 1607-1623)
- **Change:** Modified the Enter key handler to check for an active completion popup. When a popup is active with highlighted entries, pressing Enter first replaces the input buffer with the selected match, then proceeds to send it — combining acceptance and submission into one keystroke.
- **Before:** Only Tab accepted completions; Enter immediately sent raw input.
- **After:** Both Tab and Enter accept a completion entry from the popup before proceeding (Tab only sends if no popup active, Enter always submits).
