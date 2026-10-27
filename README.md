# KI-Assistent für Brim

Ein KI-Assistent in der Notch, der die KI nutzt, die schon auf deinem Rechner ist. Kein eigener API-Schlüssel, kein Abo extra.

- **Fragen per Tastenkürzel:** `Strg+Alt+K` öffnet unter der Notch ein Eingabefeld. Enter schickt, Umschalt+Enter macht eine neue Zeile, Esc schließt. Dasselbe Feld öffnet der Knopf **Fragen** in der aufgeklappten Notch und im Widget.
- **Antwort in der Notch:** Während die KI schreibt, zeigt die Notch „denkt nach …“, aufgeklappt wächst die Antwort mit. Danach steht kompakt „Antwort“ in der Notch, bis du sie aufklappst und schließt, höchstens fünf Minuten. Aufgeklappt kannst du kopieren, weiterfragen (die KI kennt das Gespräch), in Claude Desktop weitermachen oder ein neues Gespräch anfangen.
- **Text aus der Zwischenablage:** Erklären, Zusammenfassen, Übersetzen (ins Deutsche, deutschen Text ins Englische) und Verbessern mit einem Klick. Text kopieren, Knopf drücken, fertig.
- **Widget „KI-Assistent“** (groß, mittel, breit, ganz): Fragen, die Aufgaben für die Zwischenablage und die letzte Antwort.
- **Widget „KI-Apps“** (mittel, klein, breit): Claude, ChatGPT, Copilot und Perplexity mit einem Klick öffnen, je nachdem, was installiert ist.

## Welche KI das Plugin nutzt

Zum **Antworten direkt in der Notch** sucht es, in dieser Reihenfolge:

| KI | Woran es sie erkennt | Anmeldung |
|---|---|---|
| **Claude Code** | `claude` im PATH, in `%USERPROFILE%\.local\bin` oder `%AppData%\npm` | dein Claude-Konto (Pro/Max) oder API-Schlüssel, einmal im Terminal mit `claude` anmelden |
| **Ollama** | Server läuft unter `http://127.0.0.1:11434` und hat ein Modell | keine, läuft lokal |
| **LM Studio** | Server läuft unter `http://127.0.0.1:1234` (in LM Studio „Start Server“) | keine, läuft lokal |
| **Gemini CLI** | `gemini` im PATH oder in `%AppData%\npm` | dein Google-Konto, einmal im Terminal mit `gemini` anmelden |

Zum **Weiterfragen** in einer App sucht es im Startmenü nach **Claude** (Claude Desktop), **ChatGPT**, **Copilot** und **Perplexity**. „In Claude“ legt die Frage in die Zwischenablage, öffnet Claude Desktop und fügt sie ins Eingabefeld ein. Ist keine KI zum Antworten da, gehen alle Fragen so an die App.

Claude Desktop selbst hat keine Schnittstelle, über die ein anderes Programm Antworten abholen kann. Wer Antworten direkt in der Notch will, installiert deshalb zusätzlich Claude Code (`npm install -g @anthropic-ai/claude-code` oder der Installer von claude.com) und meldet sich einmal an; es nutzt dasselbe Claude-Konto.

Nach neu installierter oder gestarteter KI sieht das Plugin alle zehn Minuten nach, sofort mit **Neu suchen** in der Notch.

## Rechte

Brim fragt beim Einschalten nach dem Recht **„Netzwerk“**: für Ollama und LM Studio auf deinem Rechner und weil Claude Code und Gemini CLI mit ihren Diensten sprechen. Das Plugin selbst schickt nichts ins Internet.

Was du fragst, geht an die KI, die antwortet: bei Ollama und LM Studio bleibt es auf dem Rechner, bei Claude Code an Anthropic, bei Gemini CLI an Google. Text aus der Zwischenablage liest das Plugin nur, wenn du einen der Knöpfe dafür drückst.

## Installieren

**Aus der Bibliothek:** Brim → Einstellungen → Plugins → Bibliothek → KI-Assistent → Installieren.

**Fertig gebaut:** `brim-ai.zip` aus dem neuesten Release nach `%LocalAppData%\Brim\Plugins\ai` entpacken, Brim neu starten und im Tab **Plugins** „KI-Assistent“ einschalten.

**Selbst bauen:** Das Plugin nutzt `Brim.Sdk` aus dem Brim-Repository. Beide Repositories nebeneinander klonen:

```powershell
git clone https://github.com/wheatley2056/brim.git
git clone https://github.com/wheatley2056/brim-ai.git
cd brim-ai
dotnet publish src/Ai -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Brim\Plugins\ai"
```

Liegt Brim woanders: `-p:BrimRoot=C:\Pfad\zu\brim`.

## Einstellungen

Brim → Einstellungen → Plugins → KI-Assistent:

- **KI für Antworten in der Notch** (`auto`): die erste gefundene aus der Tabelle oben, oder fest `claude`, `ollama`, `lmstudio`, `gemini`. `aus` gibt jede Frage an die App weiter.
- **Modell** (leer): z. B. `sonnet` oder `opus` für Claude Code, `llama3.2` für Ollama, `gemini-2.5-flash` für Gemini CLI. Leer nimmt Claude Code und Gemini CLI ihr Standardmodell, Ollama und LM Studio das erste geladene.
- **App zum Weiterfragen** (`auto`): `Claude`, `ChatGPT`, `Copilot` oder `Perplexity`; `auto` nimmt die erste installierte in dieser Reihenfolge.
- **Frage in der App gleich abschicken** (aus): sonst wird sie nur eingefügt und du drückst selbst Enter.
- **Tastenkürzel** (`Ctrl+Alt+K`): z. B. `Strg+Umschalt+Leertaste` oder `Win+Shift+A`. Leer schaltet es ab. Ist es schon belegt, meldet sich die Notch.
- **Zusätzliche Anweisung an die KI**, z. B. „Duze mich“ oder „Antworte immer auf Englisch“. Ohne sagt das Plugin der KI nur, dass sie kurz und in der Sprache der Frage antworten soll.
- **Adresse von Ollama** und **von LM Studio**: für einen anderen Port oder einen Rechner im Netz (z. B. `http://gpu-pc:11434`). LM Studio steht hier für jeden OpenAI-kompatiblen Server (`/v1/chat/completions`), etwa auch llama.cpp oder Jan.

## Wenn etwas nicht klappt

Das Protokoll liegt in `%LocalAppData%\Brim\logs\plugins\de.brim.ai.log`. Beim Start steht dort, welche KI und welche Apps das Plugin gefunden hat.

- **„Keine KI gefunden“:** Eine der KIs aus der Tabelle installieren oder starten (bei LM Studio den Server), dann **Neu suchen**. Ein frisch installiertes Claude Code oder Gemini CLI findet das Plugin, sobald es im PATH steht; dafür Brim einmal neu starten oder neu anmelden.
- **„Claude Code ist nicht angemeldet“:** Terminal öffnen, `claude` eingeben und anmelden. Danach geht es ohne Terminal.
- **„Frage liegt in der Zwischenablage“:** Das Fenster der App kam nicht innerhalb von zehn Sekunden nach vorn. Die Frage mit Strg+V selbst einfügen.
- **Die Frage landet in Claude Desktop nicht im Eingabefeld:** Claude Desktop setzt den Fokus nicht immer dorthin, etwa wenn ein Dialog offen ist. Dann einmal ins Eingabefeld klicken und Strg+V drücken.
- **„Keine Antwort nach 3 Minuten“:** Die KI hängt oder rechnet sehr lange (große lokale Modelle ohne Grafikkarte). Kleineres Modell wählen.
