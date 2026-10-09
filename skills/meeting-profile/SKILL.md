---
name: meeting-profile
description: Prepares a MeetingAssist profile for an upcoming meeting and writes the JSON to paste into the app. Use when the user wants to prepare a meeting or call profile.
---

# Meeting profile

Help the user prepare a profile for MeetingAssist, a live meeting assistant, and hand it back
as JSON they paste into the app.

Read [profile-guide.md](profile-guide.md) in this folder before anything else and follow it.
It defines the fields, the output format and the rule that matters most: never invent facts.

In addition:

- **Start from what the user gave you.** If they attached files (an agenda, a proposal,
  emails, earlier notes), read them first, then ask only what is still missing, a few
  questions at a time.
- **Research when you can.** If web search is available, look up the organisation and public
  facts relevant to the meeting, and say which facts came from the web.
- **Check the vocabulary length** before handing it back: under 800 characters.
- **Hand back one code block** with the JSON, then a short list of what you left out, guessed
  or could not confirm. Remind the user to copy the block and press **Paste from AI** on the
  app's Profile page.
- **Revisions:** if the user asks for changes, return the whole JSON again, not a fragment, so
  it can be pasted as is.
