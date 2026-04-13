# Vault to Flashcard

Convert your Obsidian vault notes into Anki flashcards using AI.

## Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Anki](https://apps.ankiweb.net/) with [AnkiConnect](https://ankiweb.net/shared/info/2055492159) installed

### API Key

Set your AI API key as a user secret:

```bash
dotnet user-secrets set "ApiKey" "<YOUR_API_KEY>"
```

Supported providers: Gemini, Anthropic, MiniMax, Ollama.

## Usage

```bash
vault-to-flashcard --vault "C:\Path\To\Vault"
```

### Options

| Option                      | Description                                      | Default                |
|-----------------------------|--------------------------------------------------|------------------------|
| `-v\|--vault <PATH>`        | Path to your Obsidian vault                      | Required               |
| `-m\|--model <MODEL>`       | AI model to use                                  | gemini-3-flash-preview |
| `-p\|--provider <PROVIDER>` | AI provider (gemini, anthropic, minimax, ollama) | gemini                 |
| `-c\|--config <PATH>`       | Path to category prompt config JSON              | -                      |
| `--assets <PATH>`           | Custom assets folder path                        | vault/assets/          |
| `--skip-token <TOKEN>`      | Token marking sections to exclude                | SKIP_TOKEN             |
| `--read-only`               | Simulate without adding to Anki                  | false                  |

### Examples

Process a vault with Gemini:
```bash
vault-to-flashcard --vault "C:\MyVault"
```

Use a different model:
```bash
vault-to-flashcard --vault "C:\MyVault" --model gemini-3-pro-preview --provider gemini
```

Preview changes without modifying Anki:
```bash
vault-to-flashcard --vault "C:\MyVault" --read-only
```

## How It Works

1.  The script will scan the Obsidian vault you have provided in the `--vault` argument for files with the `study` property. 
    Those with a `true` value (`study: true`) in the YAML frontmatter will be considered for processing, and others will be skipped.
2. Notes that should be studied are split into sections based on markdown headers (`# Header 1`, `## Header 2` `### Header 3`, etc.). Use `%% SKIP_TOKEN %%` comments to exclude a section from flashcard generation.
3. The content within each header is parsed into HTML and then fed into your AI API of choice. 
   This will then create atomic flashcards based on the note formats you have provided within the `--config` argument. The "basic" (front/back) and "cloze" note types for all categories by default.
4. Using AnkiConnect the created cards are then uploaded. Decks are organised hierarchically based on the values provided in the `category` property in YAML frontmatter.
5. Once complete, a `.obsidian-anki-cache.json` file is created in your vault to cache what has been processed by the script.
6. Re-running the script will (un-)suspend cards marked with a different `study` property since the last execution, or re-create cards for header sections that have changed in content since last execution.

## Category Prompt Config

The config file lets you define custom note/card types per category. Each category matches a value from the note's `categories` YAML frontmatter property.

```json
{
  "cardTypes": [
    {
      "name": "Basic",
      "isCloze": false,
      "templates": [
        { "name": "Basic", "front": "{{Front}}", "back": "{{Back}}" }
      ],
      "jsonSchemaProperties": {
        "front": "The question or prompt for the front of the card",
        "back": "The answer for the back of the card"
      },
      "exampleOutput": "{\"front\": \"When should a Trie be used over a Hash Map?\", \"back\": \"When you need efficient prefix-based searching/auto-complete.\"}"
    },
    {
      "name": "Cloze",
      "isCloze": true,
      "templates": [
        { "name": "Cloze", "front": "{{text}}", "back": "{{text}}" }
      ],
      "jsonSchemaProperties": {
        "text": "Content with cloze deletions using {{c1::answer::hint}} format. Must have at least two clozes."
      },
      "exampleOutput": "{\"text\": \"The three main concurrency primitives in Go are: {{c1::Goroutines::lightweight threads}}, {{c2::Channels::communication mechanism}}, and {{c3::Select Statement::multiplexing mechanism}}\"}"
    },
    {
      "name": "Reversed",
      "isCloze": false,
      "templates": [
        { "name": "Forward", "front": "{{Front}}", "back": "{{Back}}" },
        { "name": "Backward", "front": "{{Back}}", "back": "{{Front}}" }
      ],
      "jsonSchemaProperties": {
        "front": "The question or prompt",
        "back": "The answer"
      },
      "exampleOutput": "{\"front\": \"What is photosynthesis?\", \"back\": \"The process by which plants convert sunlight into energy.\"}"
    },
    {
      "name": "Japanese Vocabulary",
      "isCloze": false,
      "templates": [
        { "name": "Japanese Vocabulary", "front": "{{Kanji}}", "back": "{{Reading}} — {{Meaning}}" }
      ],
      "jsonSchemaProperties": {
        "Kanji": "The Kanji representation of the vocabulary",
        "Reading": "The Kana representation of the Kanji",
        "Meaning": "The English meaning of the word"
      },
      "exampleOutput": "{\"Kanji\": \"雨\", \"Reading\": \"あめ\", \"Meaning\": \"rain\"}"
    }
  ],
  "categories": [
    {
      "category": "Programming",
      "priority": 1,
      "cardTypes": ["Basic", "Cloze"],
      "systemPromptAddendum": "Focus on code examples.",
      "skipBasicTypes": false
    },
    {
      "category": "Japanese",
      "priority": 2,
      "cardTypes": ["Japanese Vocabulary"],
      "systemPromptAddendum": "Focus on breaking down notes into easy-to-understand examples.",
      "assistantPromptAddendum": "Ensure that all kanji have accompanying Furigana using the furigana syntax: \"大丈夫[だいじょうぶ].\"",
      "skipBasicTypes": true
    }
  ]
}
```

#### Top-Level Fields

| Field        | Description                                 |
|--------------|---------------------------------------------|
| `cardTypes`  | Array of reusable card template definitions |
| `categories` | Array of category-specific configurations   |

#### Card Type Definition Fields

| Field                  | Description                                                            |
|------------------------|------------------------------------------------------------------------|
| `name`                 | Unique name for this card type                                         |
| `isCloze`              | Whether this is a cloze deletion card type                             |
| `templates`            | Array of card templates (Front/Back format for Anki rendering)         |
| `jsonSchemaProperties` | Fields this card type requires (name → description for AI prompts)     |
| `exampleOutput`        | JSON example of valid card output (used by AI for formatting guidance) |

#### Card Template Fields

| Field   | Description                                                |
|---------|------------------------------------------------------------|
| `name`  | Name of this specific template (within the card type)      |
| `front` | Front template using Anki field syntax (e.g., `{{Front}}`) |
| `back`  | Back template using Anki field syntax                      |

#### Category Fields

| Field                     | Description                                                          |
|---------------------------|----------------------------------------------------------------------|
| `category`                | Value from the note's `categories` frontmatter                       |
| `priority`                | Higher priority categories are matched first                         |
| `cardTypes`               | Array of card type names to use for this category                    |
| `systemPromptAddendum`    | Extra instructions for the AI system prompt                          |
| `assistantPromptAddendum` | Extra instructions appended to the user prompt                       |
| `skipBasicTypes`          | _(Optional, default `false`)_ Skips adding default Basic/Cloze types |

Without a config, the tool defaults to simple `Basic` and `Cloze` note types.

