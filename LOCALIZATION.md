# Translating SCSKiller

SCSKiller supports multiple languages through lightweight JSON dictionaries embedded in `SCSKiller.Core`. New languages can be added easily by the community without modifying C# code.

---

## How It Works

1. **Embedded dictionaries**: All language files live in `src/SCSKiller.Core/Localization/<code-code>.json` (for example, `en.json`, `pt-BR.json`).
2. **Automatic discovery**: SCSKiller scans embedded resources at startup and registers any valid translation dictionary.
3. **Graceful fallback**: If a string key is missing in your translation, SCSKiller automatically falls back to the English string in `en.json`.
4. **Dynamic switching**: Users can pick their preferred language in **Settings $\to$ General $\to$ Language**, or leave it on **System default** to follow their OS language.

---

## Adding a New Language

Follow these simple steps:

### 1. Copy the Base File
Make a copy of `src/SCSKiller.Core/Localization/en.json` and name it using your IETF BCP 47 culture tag (e.g., `es.json`, `de.json`, `fr.json`, `ja.json`, `zh-CN.json`).

### 2. Configure the Metadata
At the top of your JSON file, adjust the `_meta` object:

```json
{
  "_meta": {
    "code": "es",
    "nativeName": "Español",
    "englishName": "Spanish"
  },
  "Title_Subtitle": "Eliminador de tartamudeo en compilación de shaders",
  ...
}
```

- `code`: The culture code matching the file name without extension.
- `nativeName`: The name of the language as written in that language (this is what appears in the Settings dropdown).
- `englishName`: The English name of the language.

### 3. Translate the Values
- Only edit the values on the right side of the colon. Keep the keys on the left unchanged.
- Preserve format specifiers such as `{0}`, `{1}`, etc. Exactly match the position and count of parameters in the original string.

### 4. Submit a Pull Request
1. Commit your changes with Developer Certificate of Origin (DCO) sign-off:
   ```bash
   git commit -s -m "Add <Language> translation"
   ```
2. Open a Pull Request on GitHub.

---

## Included Translations

- **English (`en`)**: Base strings.
- **Português do Brasil (`pt-BR`)**: Brazilian Portuguese translation.
- **简体中文 (`zh-Hans`)**: Simplified Chinese translation (adapted and completed from JohnsonRan's PR #68).


