# HTML editor checks

Run the local web host, then from the repository root:

```powershell
npm ci --ignore-scripts --prefix tests/NaSeqMen.WebChecks
npm test --prefix tests/NaSeqMen.WebChecks
```

The tests use Playwright 1.65.1 and an already installed Google Chrome in headless mode. No browser download is required. Override `NASEQMEN_TEST_CHROME` for another installed executable and `NASEQMEN_TEST_URL` for another local host URL (default `http://127.0.0.1:17964`).

All `/api/**` requests are fulfilled by test fixtures. The browser WebSocket constructor is replaced before application startup. No diagnostic subscription, configuration mutation or action reaches the real executor. The test profile does not read the user's browser profile.

Coverage: SVG graph loading; log following at the end, paused scrolling above the end, resumption and retained reading position during the 400-row trim; device observation, repeat/down/up grouping, separate interfaces, wheel pulse, stop/disconnect; mapping/node/connection dialogs; connection secret omission; console errors; 1280×820 and 900×600 layouts.

TechnicalShell coverage: Cream/Nano defaults, unchanged theme order and full appearance menu, density without changing font size, panel borders, preference persistence, sidebar tree and collapse, RU/EN, custom keyboard tooltips and Escape, tooltip disabling in dialogs, and fixed shell scrolling.

Screenshots and the result JSON are written under ignored `local/`. `node_modules/` is ignored by this test project's `.gitignore`.
