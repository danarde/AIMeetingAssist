# How to write a MeetingAssist profile

This guide is for any AI assistant, or person, preparing a profile for **MeetingAssist**. The
user gives you this file and what they know about an upcoming meeting. You help them turn
that into a profile, and hand it back as JSON they paste into the app.

## What the profile is for

MeetingAssist transcribes a live online meeting: the user's microphone, and everyone else
through the computer's audio. When the user presses a hotkey, the app sends the last minute
or so of conversation, plus this profile, to a language model. The model replies with at most
five short bullets that the user glances at while they keep talking.

Apart from the conversation itself, the profile is all the model knows. A good profile means
the bullets hold the right figure, name or prepared answer at the moment it is needed. A vague
profile means generic advice. Write for a reader who has three seconds to read a cue.

It works for any meeting the user can prepare for: a client check-in, a sales call, a supplier
negotiation, a salary review, a project kickoff, a parent-teacher meeting.

## How to work

1. **Find out what the meeting is.** Ask who it is with, what the user wants out of it, and
   what material they have: an agenda, a proposal, an email thread, earlier notes. Ask a
   few questions at a time, not a long questionnaire.
2. **Research if you can.** If you can search the web, look up the organisation and public
   facts that matter to the meeting. Tell the user what came from the web.
3. **Never invent facts.** Do not make up figures, dates, prices, names, achievements or
   commitments. If a fact would help and you do not have it, ask. If the user does not know,
   leave it out. An invented fact is worse than a missing one: the user may say it aloud.
4. **Draft the answers, and let the user decide the ones that matter.** You can draft answers
   from the user's material. Where the answer is the user's decision (a salary figure, a
   deadline, what they will concede), ask them instead of choosing.
5. **Keep it dense.** Facts, numbers, names, short phrases. No introductions, no filler. As a
   guide: *This meeting* under 150 words, *About me* under 250, and 5 to 12 questions.
6. **Hand it back.** Write the JSON in a single code block, so it can be copied in one go.
   After it, list in a few lines anything you left out, guessed or could not confirm.

## The fields

| Key | Required | What goes in it |
|---|---|---|
| `name` | yes | Short identifier, lowercase with hyphens: `northwind-checkin`, `fabrikam-renewal-2`. It becomes the file name. |
| `meeting` | yes | **This meeting.** The goal, who is attending and who they are (name, role, what they care about), the agenda, what a good outcome looks like. Things the user wants to ask the other side belong here too. |
| `aboutMe` | yes | **About me.** What the user brings: their role, relevant experience, the stories and figures they may want to mention, their offer or position. |
| `questions` | yes | **Likely questions, with my answers.** Questions or pushback the user expects, one per line, each followed by the answer they want to give. See the format below. |
| `vocabulary` | yes | Words a speech recogniser may mishear: names of people, companies, products, places, technologies, acronyms. Comma-separated, no sentences. **Under 800 characters**: the recogniser silently drops anything past its limit. |
| `language` | yes | The language the meeting is held in, as a two-letter code: `en`, `es`, `de`. Both the transcription and the bullets use it. |
| `micLabel` | yes | How the user appears in the transcript: their first name, or `You`. |
| `loopbackLabel` | yes | How everyone else appears. Everyone on the other side shares one audio channel, so this is one label: a name when it is one person (`Priya`), otherwise a role or organisation (`Sales team`, `Northwind`). |
| `mockBrief` | no | Only if the user wants to rehearse: the app can have an AI play the other side by voice. Describe who they are, what they will ask and how tough to be. Otherwise leave it out. |

Leave out `answerStyle`. It changes how the bullets are written, and the app's default works.

### The questions format

One question per line, the answer after an arrow:

```
"Can you deliver by March?" -> Phase one yes; phase two needs the API spec by 1 February
"Your rate is above budget" -> Fixed-price option: 6 days, payment on delivery
```

Write each answer as a cue, not a script: the figure, the name or the story's keyword, so it
can be glanced at.

## Output format

Plain JSON, all values strings, line breaks inside a value written as `\n`:

```json
{
  "name": "northwind-checkin",
  "meeting": "...",
  "aboutMe": "...",
  "questions": "\"...\" -> ...\n\"...\" -> ...",
  "vocabulary": "...",
  "language": "en",
  "micLabel": "You",
  "loopbackLabel": "Northwind",
  "mockBrief": ""
}
```

The user copies it and presses **Paste from AI** on the app's Profile page, which makes a new
profile from it. The app never overwrites an existing profile from a paste.

Write the contents in the language of the meeting or in English; both work. The vocabulary
must be spelled as the words will be spoken in the meeting.

## Examples

These are fictional. They show the level of detail, not facts to reuse.

### A supplier negotiation, with a rehearsal brief

```json
{
  "name": "fabrikam-renewal",
  "meeting": "Annual contract renewal with Fabrikam, our packaging supplier. Attending: Jonas Weber (key account manager) and Elena Costa (their finance lead). 45 minutes. Fabrikam announced an 8% price rise. Goal: renew for two years at no more than 3%. Walk-away: 5% with 60-day payment terms. To ask: what drives the increase; whether volume tiers are possible.",
  "aboutMe": "Procurement lead at Contoso Foods; I own this contract. Last year we bought 1.2 million units, up 15%, and paid every invoice on time. Two other quotes in hand: Northwind Packaging at 4% above today's price, Litware at 6% with longer lead times.",
  "questions": "\"Raw material costs are up 12%\" -> Ask which index; our volume grew 15%\n\"8% is our standard increase\" -> Counter at 2% for a two-year commitment\n\"Can you commit to more volume?\" -> Up to 1.4 million units if the price holds at 3%\n\"Are you talking to other suppliers?\" -> Yes, we have alternatives; we would rather stay",
  "vocabulary": "Fabrikam, Contoso Foods, Jonas Weber, Elena Costa, Northwind Packaging, Litware, volume tier, payment terms, lead time, price index",
  "language": "en",
  "micLabel": "You",
  "loopbackLabel": "Fabrikam",
  "mockBrief": "Play Jonas Weber, the key account manager. Polite but firm on the 8%. Justify it with raw material costs, offer a discount only for a three-year deal, and push back once on a vague answer."
}
```

### A client project check-in

```json
{
  "name": "northwind-checkin",
  "meeting": "Fortnightly check-in on the Northwind data warehouse migration. Attending: Priya Shah, Northwind's head of data, who decides scope, and Tom Becker, their platform engineer. Goal: confirm the 14 March cut-over date and agree how the extra reporting work is handled.",
  "aboutMe": "Lead consultant on the migration since September. 31 of 40 pipelines are moved and the nightly load is down from 5 hours to 70 minutes. Of the 9 left, 3 depend on the CRM export, which Northwind owns.",
  "questions": "\"Will we make 14 March?\" -> Yes for 37 pipelines; the 3 CRM ones need the export fixed by 28 February\n\"Why is the reporting work extra?\" -> It was outside the signed scope: 6 days, and it can start after cut-over\n\"Can Tom's team take over support?\" -> Yes: two handover sessions the week after cut-over",
  "vocabulary": "Northwind, Priya Shah, Tom Becker, cut-over, Snowflake, dbt, Airflow, CRM export, pipelines, data warehouse",
  "language": "en",
  "micLabel": "You",
  "loopbackLabel": "Northwind"
}
```

### A salary review, in Spanish

```json
{
  "name": "revision-salarial-2026",
  "meeting": "Revisión salarial anual con Carmen López, mi responsable directa. 30 minutos. Objetivo: subida al rango de senior, 52.000 €. Mínimo aceptable: 48.000 € más un día de teletrabajo extra. Preguntar: cuándo se revisan las bandas y qué falta para el siguiente nivel.",
  "aboutMe": "Analista de datos desde 2022. Este año: automaticé el informe mensual (de 3 días a 2 horas), lideré la migración a Power BI, formé a 12 personas. Salario actual 44.000 €. Mercado para el puesto en Madrid: 50.000-56.000 €.",
  "questions": "\"No hay presupuesto este año\" -> Propuesta escalonada: 48.000 € ahora, 52.000 € en la revisión de julio\n\"Tu rendimiento es bueno pero no excepcional\" -> Pedir ejemplos concretos; recordar el informe automatizado\n\"¿Tienes otra oferta?\" -> No hablar de ofertas; centrarse en el mercado",
  "vocabulary": "Carmen López, Power BI, banda salarial, teletrabajo, informe mensual",
  "language": "es",
  "micLabel": "Yo",
  "loopbackLabel": "Carmen"
}
```
