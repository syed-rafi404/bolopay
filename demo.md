# BoloPay — Voice-First Bangla Transaction Safety Layer
**Build spec & methodology — hand this whole file to your coding agent as the starting prompt.**

Working title only — rename freely. "Bolo" (বলো) = "say/speak."

---

## 0. How to use this document

This is written to be handed directly to an autonomous coding agent (e.g. Claude Code) with an instruction like *"Build this end-to-end, following the spec exactly, and ask me only when you hit a ⚠️ item."*

Two label conventions used throughout:
- **✅ Confirmed** — verified against official documentation on **August 7, 2026**. Safe to build against directly.
- **⚠️ Verify / tune** — a reasonable starting point, not a measured fact. The agent should either check it against current docs before depending on it, or the number needs empirical calibration against real test recordings before the feature can be trusted. Do not silently treat these as settled.

Section 17 collects every ⚠️ in one place as a checklist.

---

## 1. Context & why this exists

This is a portfolio piece built for a job application to Banglalink's Strategic Assistant Program. Banglalink is currently building **Mukto Pay**, a new mobile payments platform (PSP license granted by Bangladesh Bank, Dec 2025; build partner Huawei, announced March 2026), aimed substantially at first-time digital-finance users in a country where a large share of adults are still unbanked.

The premise: voice is a more natural input than a banking UI for a lot of that target population — but ASR makes mistakes, and in a financial context a misheard digit means money going to the wrong place or the wrong amount. This project demonstrates a **voice transaction flow with a built-in uncertainty detector**: when the speech-to-text model wasn't confident about what it heard — especially in the amount or recipient — the app doesn't silently proceed. It visibly flags the uncertain part and forces the user to confirm or correct it before anything "sends."

This mirrors an existing hallucination-detection metric the author built for a separate Bangla ASR research project (repetition/anomaly flagging via transcription-quality signals). Here the same underlying idea — using a transcription model's own quality signals to decide when to distrust its output — is repointed at transaction safety instead of transcript quality.

**This is a demo, not a bank.** No real money, no real accounts, no connection to Banglalink or Mukto Pay systems. That must be visible in the UI (see Section 16).

---

## 2. Goals

- A **live, public URL** that works reliably when a stranger (recruiter) opens it cold, on desktop or mobile, with or without granting mic permission.
- A working pipeline: **voice → transcript → confidence check → structured intent → confirm/correct → mock receipt.**
- The confidence-check step must be real (driven by actual ASR quality metadata), not a canned/scripted "gotcha."
- Cheap or free to run indefinitely, and resistant to a stranger accidentally (or deliberately) running up API usage.
- Looks intentional and finished — this is going on a CV, not a hackathon repo.

## 3. Non-goals (explicitly out of scope — do not build these)

- No real payment rails, banking APIs, or KYC. Everything financial is mocked.
- No user accounts / login / signup.
- No model training or fine-tuning. Everything runs through hosted APIs.
- No native mobile app — responsive web only.
- No attempt at full Bangla dialect coverage — standard Bangla + common numeral phrasing is the target, not every regional variant.
- No text-to-speech voice readback in v1 (see 5.4 for why) — on-screen Bangla confirmation text instead.
- No persistent database in v1 (see 5.3 for why) — session/client-side mock state only.

---

## 4. User flow

1. Landing page: mock account card (name, phone, starting balance ৳5,000), 3 seed contacts, a big mic button, a row of "▶ Try a sample command" chips, and a visible one-line disclaimer (Section 16).
2. User either (a) taps the mic and speaks, or (b) taps a sample chip to play a pre-recorded clip through the real pipeline. **(b) must exist** — don't assume every visitor grants mic access or wants to attempt Bangla pronunciation on a work laptop.
3. Audio → backend → Groq Whisper transcription → confidence scoring → LLM intent extraction → contact matching.
4. Confirmation screen renders: *"Send ৳[amount] to [recipient]?"*
   - If confidence is fine: amount/recipient shown as plain text, one Confirm button.
   - If confidence is flagged: the uncertain field renders as an **editable input pre-filled with the ASR's best guess**, amber-highlighted, with a short note ("Wasn't fully sure about this — please check it") and the button copy changes to make the correction step impossible to miss.
5. Confirm → mock receipt screen (transaction id, amount, recipient, timestamp) → balance updates in local state.
6. "Try another command" resets to step 2.
7. Unrecognized / off-topic speech → a friendly "Didn't catch a valid command — try 'send [amount] to [name]' or 'what's my balance'" state, not a crash.

---

## 5. Architecture

```
Browser (React)
  ├─ MediaRecorder (mic capture) OR pre-recorded sample clip
  ↓ audio blob (webm/opus)
Next.js API Route: /api/process-voice-command
  ├─ 1. POST audio → Groq Whisper (whisper-large-v3-turbo)
  │      → transcript + segments[] (avg_logprob, no_speech_prob, compression_ratio)
  ├─ 2. Confidence scoring (Section 7) → isLowConfidence, reasons
  ├─ 3. POST transcript → Groq LLM (gpt-oss-120b), structured JSON output
  │      → { intent, amount_bdt, recipient_name, raw_number_phrase }
  ├─ 4. Fuzzy-match recipient_name against seeded mock contacts
  └─ 5. Return combined result to browser
Browser
  ├─ Render confirm/edit screen
  ├─ On confirm: update local mock balance (React state), show receipt
```

### 5.1 Why Groq for ASR
`whisper-large-v3-turbo` on Groq is the same model family already used and benchmarked (in Bangla) in the author's prior thesis work — genuine continuity, not a new tool picked for this demo. It's also fast and has a workable free tier (see 6.1).

### 5.2 Why a second LLM call instead of parsing the transcript with regex
Bangla number words, casual phrasing, and code-switching (Banglish) make rule-based parsing brittle. A single structured-output LLM call is more robust and is itself demonstrable AI-engineering judgment, not just plumbing.

### 5.3 Why no database in v1 — decide before building
A demo only needs to work convincingly *once per visit*. Adding Postgres/Redis for persistence adds a whole category of things that can silently break in production (connection strings, cold starts, migration state) for zero demo value. **Default: pure client-side mock state (React), reset on refresh.** The API routes are stateless proxies to Groq only. If you want persistence later (e.g. a running "recent transactions" feed across visitors) that's a clearly separable Phase 2 — see 10, Phase 9 — using Upstash Redis via Vercel's integration marketplace, not a blocker for calling v1 done.

### 5.4 Why no TTS voice readback in v1
Bangla (bn-BD) support in the browser's built-in `speechSynthesis` API is inconsistent across browsers/OS (reasonably common on Chrome/Android, spotty elsewhere), and a good TTS API adds another provider/cost surface for marginal benefit — the input is already voice, which is what "voice-first" is actually claiming. On-screen Bangla confirmation text is faster to build and more reliable. Optional stretch goal only, gated behind feature-detecting an available `bn-*` voice — never assume it exists.

---

## 6. External API contracts

### 6.1 Groq — Speech to Text ✅ Confirmed (console.groq.com/docs/speech-to-text, checked 2026-08-07)

```
POST https://api.groq.com/openai/v1/audio/transcriptions
Authorization: Bearer ${GROQ_API_KEY}
Content-Type: multipart/form-data

file: <audio blob>
model: "whisper-large-v3-turbo"
language: "bn"
response_format: "verbose_json"
timestamp_granularities[]: "segment"
temperature: 0
```

Notes confirmed from official docs:
- Supported upload formats include `webm` — no client-side transcoding needed for standard `MediaRecorder` output.
- Free tier: 25MB max file size (a few seconds of speech is a few hundred KB — nowhere close).
- Minimum billed length is 10 seconds regardless of actual clip length — irrelevant for cost here (Whisper-large-v3-turbo is $0.04/hour, so even worst-case abuse is fractions of a cent per request), but don't be surprised the billing dashboard shows more usage than raw audio duration.
- `word` timestamp granularity gives **only** word + start/end time — it does **not** include per-word confidence. Confidence-related fields (`avg_logprob`, `no_speech_prob`, `compression_ratio`) exist **only at the segment level**. Design around this — see Section 7.
- ⚠️ **Verify**: Groq's model catalog changes frequently (they deprecated several LLM models with weeks' notice in mid-2026). Re-check `whisper-large-v3-turbo` is still current at console.groq.com/docs/models before deploying. `whisper-large-v3` (higher accuracy, slower, no translation needed here) is the fallback if turbo is ever retired.

Example call (Next.js Route Handler, Node runtime):

```typescript
// lib/groq-asr.ts
export async function transcribeAudio(audioBlob: Blob) {
  const form = new FormData();
  form.append('file', audioBlob, 'command.webm');
  form.append('model', 'whisper-large-v3-turbo');
  form.append('language', 'bn');
  form.append('response_format', 'verbose_json');
  form.append('timestamp_granularities[]', 'segment');
  form.append('temperature', '0');

  const res = await fetch('https://api.groq.com/openai/v1/audio/transcriptions', {
    method: 'POST',
    headers: { Authorization: `Bearer ${process.env.GROQ_API_KEY}` },
    body: form,
  });

  if (!res.ok) throw new Error(`Groq ASR failed: ${res.status} ${await res.text()}`);
  return res.json() as Promise<{
    text: string;
    segments: Array<{
      id: number; text: string; start: number; end: number;
      avg_logprob: number; no_speech_prob: number; compression_ratio: number;
    }>;
  }>;
}
```

### 6.2 Groq — LLM structured extraction ✅ Confirmed model availability; ⚠️ verify schema support at build time

As of Aug 2026, Groq **deprecated** `llama-3.3-70b-versatile` and `llama-3.1-8b-instant` (mid-June 2026, weeks' notice). Do **not** use those model IDs — they will fail. Current recommended text models: `openai/gpt-oss-120b` (larger, recommended for the retired 70B use case) and `openai/gpt-oss-20b` (smaller/faster).

**Default: `openai/gpt-oss-120b`.**

```
POST https://api.groq.com/openai/v1/chat/completions
Authorization: Bearer ${GROQ_API_KEY}
Content-Type: application/json

{
  "model": "openai/gpt-oss-120b",
  "temperature": 0,
  "response_format": {
    "type": "json_schema",
    "json_schema": {
      "name": "voice_command",
      "schema": {
        "type": "object",
        "properties": {
          "intent": { "type": "string", "enum": ["send_money", "check_balance", "unrecognized"] },
          "amount_bdt": { "type": ["number", "null"] },
          "recipient_name": { "type": ["string", "null"] },
          "raw_number_phrase": { "type": ["string", "null"] }
        },
        "required": ["intent", "amount_bdt", "recipient_name", "raw_number_phrase"]
      }
    }
  },
  "messages": [
    { "role": "system", "content": "<see prompt below>" },
    { "role": "user", "content": "<transcript text from 6.1>" }
  ]
}
```

Starting system prompt:
> You are a transaction-command parser for a Bangladeshi mobile payment app. You'll receive a Bangla or Banglish transcript of a spoken command. Classify intent as `send_money`, `check_balance`, or `unrecognized`. For `send_money`, extract the amount in BDT as a number — convert Bangla number words to digits (e.g. পাঁচশো → 500, নয়শো → 900, হাজার → 1000) — and the recipient's name as spoken. Also return the exact substring of the transcript containing the number, verbatim, as `raw_number_phrase`. Never guess an amount that wasn't stated; return null instead. Respond only with JSON matching the schema.

⚠️ **Verify before relying on it**: whether `gpt-oss-120b` supports `response_format: json_schema` on Groq specifically (docs suggest yes, but test on Day 1). If it errors, fall back to `{"type": "json_object"}` mode (must include the literal word "JSON" somewhere in your prompt) or swap providers for this one call — Claude Haiku 4.5 (`claude-haiku-4-5-20251001` via the Anthropic API) is a solid fallback with strong structured-output support and good multilingual handling; it's paid rather than free-tier, but at this request volume the cost is negligible.

⚠️ **Verify**: `gpt-oss-120b`'s actual Bangla-extraction quality is untested by us. Test it against 5–10 real transcripts on Day 1 (Phase 3 in Section 10). If it mangles Bangla number words or produces inconsistent JSON, switch to the Claude Haiku fallback above rather than fighting the prompt indefinitely.

---

## 7. The safety-net algorithm (this is the actual differentiator — spend the most care here)

**Important honesty check, not a nice-to-have:** because confidence metadata is segment-level only (6.1), and a short 2–4 second command is very likely to come back as a **single segment**, the realistic behavior is *"flag the whole utterance for confirmation,"* not *"highlight the specific misheard digit."* That's fine — in a financial-safety context, over-flagging the whole transaction when uncertain is the defensible choice, not a limitation to hide. Frame it that way if asked about it in an interview: it's a considered trade-off, not a shortfall.

```typescript
// lib/confidence.ts
interface WhisperSegment {
  text: string;
  avg_logprob: number;
  no_speech_prob: number;
  compression_ratio: number;
}

// ⚠️ STARTING POINTS ONLY — derived from Groq's own documented interpretation
// guidance (their "healthy" example was avg_logprob ≈ -0.10, no_speech_prob ≈
// 0.01, compression_ratio ≈ 1.66). These have NOT been measured against real
// audio. Before trusting this feature, run it against your own test clips
// (Section 14), log the actual field values, and move these thresholds to
// wherever clean vs. deliberately-mumbled recordings actually separate.
const THRESHOLDS = {
  avgLogprobFloor: -0.5,
  noSpeechProbCeiling: 0.4,
  compressionRatioMax: 2.2,
  compressionRatioMin: 1.0,
};

export function scoreConfidence(segments: WhisperSegment[]) {
  const reasons = new Set<string>();
  let worstSegment: WhisperSegment | null = null;
  let worstLogprob = Infinity;

  for (const seg of segments) {
    if (seg.avg_logprob < THRESHOLDS.avgLogprobFloor) reasons.add('low_confidence');
    if (seg.no_speech_prob > THRESHOLDS.noSpeechProbCeiling) reasons.add('possible_non_speech');
    if (seg.compression_ratio > THRESHOLDS.compressionRatioMax
        || seg.compression_ratio < THRESHOLDS.compressionRatioMin) reasons.add('unusual_pattern');

    if (seg.avg_logprob < worstLogprob) {
      worstLogprob = seg.avg_logprob;
      worstSegment = seg;
    }
  }

  return {
    isLowConfidence: reasons.size > 0,
    reasons: [...reasons],
    worstSegment,
  };
}
```

Wire the result into the API response as `needs_confirmation: boolean` plus a short human-readable `confidence_reason` the frontend can show next to the flagged field (e.g. "background noise detected" for `possible_non_speech`, "wasn't fully sure about this" as a generic fallback).

---

## 8. Data model (mock only — no real backend)

```typescript
// lib/mock-contacts.ts
export const MOCK_ACCOUNT = { name: 'Rafi', phone: '01711-XXXXXX', balanceBdt: 5000 };

export const MOCK_CONTACTS = [
  { name: 'Adiba', phone: '01911-XXXXXX' },
  { name: 'Tanvir', phone: '01611-XXXXXX' },
  { name: 'Amma', phone: '01811-XXXXXX' },
];
```
Fill in names/framing to taste. Recipient matching: try exact (case-insensitive) match first, then a simple string-similarity fallback (e.g. the `string-similarity` npm package) — don't build anything fancier than that, it's not the point of the demo.

Balance/transaction state lives in React state on the client, seeded from the constants above on page load. A receipt object (`{id, amount, recipient, timestamp}`) is generated client-side after confirm — a mock `/api/mock-transaction` route that just validates and echoes it back is fine for architectural realism but isn't required to persist anything.

---

## 9. Project structure

```
/app
  page.tsx                            — main demo UI
  layout.tsx
  globals.css
  /api/process-voice-command/route.ts — the pipeline endpoint (Section 6+7)
/components
  MicButton.tsx
  SampleClipPicker.tsx
  ConfirmationCard.tsx                — handles both plain + editable/flagged states
  ReceiptCard.tsx
/lib
  groq-asr.ts
  groq-llm.ts
  confidence.ts
  mock-contacts.ts
  types.ts
/public/sample-audio/*.webm           — pre-recorded demo clips (Section 14)
.env.example
README.md
```

---

## 10. Build phases — build and sanity-check in this order, don't jump ahead

1. **Scaffold**: `create-next-app` (TypeScript, App Router, Tailwind). Deploy an empty placeholder page to Vercel immediately — confirm the deploy pipeline itself works before adding any complexity.
2. **ASR wrapper only**: a bare test route that accepts an uploaded file and returns raw Groq transcript + segments JSON. No UI. Verify against 1–2 real Bangla clips.
3. **Confidence scoring**: layer Section 7 on top of real segment output from step 2. Print the actual `avg_logprob`/`no_speech_prob`/`compression_ratio` values for a clean clip vs. a deliberately mumbled one (Section 14) and adjust `THRESHOLDS` until they visibly separate.
4. **LLM extraction**: wire up Section 6.2. Test against several transcript variations, including nonsense input and a check-balance phrasing, not just the happy path.
5. **Frontend — record + confirm flow**: mic capture, the plain confirmation screen, then the editable/flagged variant.
6. **Mock ledger + receipt**: client-state balance updates, contact matching, receipt screen.
7. **Sample-clip picker**: the pre-recorded fallback path (Section 4, step 2b) — treat this as required, not optional.
8. **Rate limiting + error states + disclaimer copy** (Sections 11, 16): mic-permission-denied handling, API-failure handling, loading states.
9. *(Optional, later)* Persistence via Upstash Redis if you want transaction history to survive a refresh — not required for v1.
10. **Deploy for real** (Section 12), then test the *live* URL end-to-end on both a desktop and a phone before calling it done — not just the local dev server.
11. **README**: short project description, architecture note, and a line connecting it back to why it exists (Section 1) — useful for anyone who clicks through from the CV to the repo.

---

## 11. Environment variables

```bash
# .env.example
GROQ_API_KEY=              # required
ANTHROPIC_API_KEY=         # optional — only if using the Claude Haiku fallback (6.2)
UPSTASH_REDIS_REST_URL=    # optional — only if adding Phase-2 persistence/rate limiting
UPSTASH_REDIS_REST_TOKEN=  # optional
```

---

## 12. Rate limiting & abuse protection

This will be linked from a public CV, so a stray bot or a bored visitor spamming the mic button shouldn't be able to do anything expensive or embarrassing. The realistic dollar risk here is tiny (Whisper-turbo is $0.04/hour of audio; a determined abuser burning through the whole 2,000-request/day free tier costs about as much as it sounds — cents), so this is really about **not silently exhausting the free tier and breaking the demo for real visitors**, not about real financial exposure.

- First choice: Vercel's built-in Firewall/WAF rate limiting on the `/api/process-voice-command` route, configured in the dashboard — no extra code or service dependency. ⚠️ Verify this is available on the Hobby tier when you set this up; some Firewall features are Pro-only.
- If it isn't available on Hobby, fall back to a minimal Upstash-Redis-backed counter (a sliding window, ~20 requests/IP/hour is generous for a real demo user and low enough to bound worst case).
- Client-side: cap recordings at ~15 seconds and disable the mic button while a request is in flight.

---

## 13. Deployment (Vercel) ✅ Confirmed limits (vercel.com/docs/limits, checked 2026-08-07)

- Hobby tier serverless function duration: 10s default, **configurable up to 60s max**. Set `maxDuration: 30` on the `/api/process-voice-command` route explicitly (via route segment config or `vercel.json`) rather than relying on the tight default — the actual pipeline (short-clip transcription + one LLM call) should run in a few seconds given Groq's stated speed, but 30s gives real margin against cold starts/network hiccups without needing Pro.
- Hobby usage caps (bandwidth 100GB/mo, ~1M function invocations/mo) are far beyond what a CV demo will ever see.
- Steps: push to GitHub → import repo in Vercel → set the env vars from Section 11 in the Vercel project settings → deploy → test the live URL, not just the preview.

---

## 14. Demo script / sample clips — this is Rafi's task, not the agent's

The agent cannot generate Bangla audio. **You need to record 4–5 short clips yourself** and drop them in `/public/sample-audio/`, wired to the "Try a sample command" chips (Section 4):

1. A clean, clearly-spoken send-money command (e.g. "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও").
2. A second clean one with a different amount/recipient, for variety.
3. A **deliberately mumbled or noisy** version of a send-money command — mumble the number specifically, or record with background noise — this is the clip that should visibly trigger the edit/flag UI. This is the single most important clip in the whole demo; a recruiter watching this one fire is the entire point of the project.
4. A check-balance command.
5. Something off-topic/nonsense, to show the graceful "didn't catch that" state rather than a crash.

Use these same clips during Phase 3 (Section 10) to calibrate the confidence thresholds — don't guess at the numbers, read the actual logged values.

---

## 15. Acceptance criteria — definition of done

- [ ] Live public URL loads cleanly, no console errors, on both desktop Chrome and a phone browser.
- [ ] All 5 sample clips (Section 14) play through the *real* pipeline (not hardcoded results) and produce correct/sensible outcomes.
- [ ] Clip #3 visibly triggers the edit/flag UI — confirmed by actually watching it happen, not assumed.
- [ ] Live mic recording works on both desktop Chrome and mobile Safari — ⚠️ test both explicitly; `MediaRecorder` codec support differs by browser (Safari's default output format isn't always the same as Chrome's), so verify rather than assume `webm` works everywhere your visitors might be.
- [ ] Unrecognized speech produces the friendly fallback state, not an error screen.
- [ ] Mobile-responsive layout.
- [ ] Disclaimer (Section 16) visible without scrolling.
- [ ] README explains what this is and why it exists.
- [ ] Tested after a *fresh* deploy from a clean clone — not just "works in the dev server."

---

## 16. Required UI copy (disclaimer)

Somewhere visible on the landing screen, without needing to scroll or click through:

> *This is an independent portfolio prototype exploring voice-based transaction safety, inspired by Bangladesh's growing mobile-payments sector. It is not affiliated with, endorsed by, or connected to Banglalink, Mukto Pay, or any bank. No real money, accounts, or personal data are involved — everything here is simulated.*

This matters for two reasons: it's honest, and it protects against the demo reading as overreach rather than as a thoughtful, self-aware piece of work — which is the actual goal.

---

## 17. Everything flagged ⚠️ in one place — resolve before trusting the build

- Confirm `whisper-large-v3-turbo` is still Groq's current model before deploying (6.1).
- Confirm `gpt-oss-120b` actually supports `response_format: json_schema` on Groq (6.2) — test Day 1.
- Confirm `gpt-oss-120b`'s Bangla extraction quality is good enough; have the Claude Haiku fallback ready if not (6.2).
- The confidence thresholds in Section 7 are starting points from Groq's documented interpretation guidance, not measurements — calibrate against your own recordings (Phase 3, Section 10).
- Vercel Firewall/WAF rate limiting's availability on the Hobby tier is unconfirmed — check when you set it up (Section 12).
- Cross-browser `MediaRecorder` codec behavior (esp. Safari) is untested — verify, don't assume (Section 15).
- Groq's model catalog changes with weeks' notice (they deprecated several models in mid-2026) — re-check console.groq.com/docs/models isn't showing deprecation warnings for whatever's actually deployed, at build time and again before the interview.