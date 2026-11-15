# Localization contract

English is the primary product language. A new editor profile starts in English; the EN/RU control persists the user's choice under `naseqmen.ui.language`. Browser and WebView2 profiles have independent preferences.

Built-in copy belongs to catalogs, not executable logic or markup. The HTML editor uses stable keys (`ui.*`, `forms.*`, `graph.*`), English source resources and matching Russian resources. `data-i18n`, `data-i18n-label` and `data-i18n-tooltip` bind text, accessible names and the shared TechnicalShell tooltips. Switching language updates open forms without discarding edits. Do not use Russian or English sentences as lookup keys.

The executor exposes its static catalog through `GET /api/localization`. Diagnostic messages carry `messageCode` and `messageArgs`, with an English `message` fallback. Validation includes structured `issues` and retains English `errors` for older clients. API errors use `code`, `args` and an English fallback. Nested structured arguments are localized recursively. Module, operation and device descriptors can provide `nameKey`; their canonical `name` remains English. This metadata is not event retention or state synchronization.

The editor formats known messages at display time. Changing language also updates messages already visible in the current diagnostic view; it does not request past events. Unknown external messages, operating-system errors, compiler output, protocol identifiers and user-written names/scripts retain their original content. Never guess translations by substring or change saved user configuration to translate the UI.

Native startup copy uses .NET resources with English as the neutral language. The preserved legacy WPF editor continues to consume compatible English fallback fields; the active international UI is the HTML/WebView2 editor.

Run `node tests/NaSeqMen.WebChecks/localization-checks.mjs` to check resource completeness, placeholder parity, default English, structured backend messages and preservation of user data. Browser checks verify the actual language switch and existing editor interactions.
