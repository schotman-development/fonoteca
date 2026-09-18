/**
 * How the attribution outcomes read on screen.
 *
 * Shared between the album list, the album page and the matching screens so that
 * one answer never carries two names. The first three are what can appear on a
 * filed album; the four below them are the ones that file nothing, and they were
 * added when the matching screens arrived — a refused file has no release page,
 * but it does have a question, and the question has to say what it is answering.
 *
 * `Attributed` deliberately renders as nothing at all. It is the ordinary case
 * and a badge on every album would be noise that trains the eye to skip the two
 * badges that matter.
 *
 * `short` is the same answer at tile width, and it lives here rather than in the
 * album grid for the reason the whole file exists: a second name invented at the
 * call site is a second name that drifts. A Badge is a chip — it does not wrap
 * and it does not truncate — so a 25-character label in a 160px tile does not
 * shrink, it simply hangs over the edge of the card.
 */
export const CERTAINTY: Readonly<
  Record<
    string,
    {
      readonly label: string
      readonly short: string
      readonly tone: 'ok' | 'warning' | 'neutral'
      readonly note: string
    }
  >
> = {
  Attributed: {
    label: 'Certain',
    short: 'Certain',
    tone: 'ok',
    note: 'One release fitted these files, and nothing else fitted as well.',
  },
  AttributedAmbiguously: {
    label: 'One of several pressings',
    short: 'Several pressings',
    tone: 'warning',
    note:
      'Several editions fitted exactly as well and agreed about where every track sits, so one was ' +
      'chosen — official first, then earliest. Nothing the catalogue stores differs between them, ' +
      'but the choice was a coin flip.',
  },
  GroupOnly: {
    label: 'Album known, pressing not',
    short: 'Pressing unknown',
    tone: 'warning',
    note:
      'The editions that fitted disagree about which disc and track this music sits on, so no ' +
      'pressing was named — a track number here would be an invention.',
  },

  /*
    The two a person writes. `AttributedByPerson` is not `Attributed` and the
    difference is the point of printing it: one means a rule cleared its gates
    and the other means it did not and somebody decided anyway. On the folder
    listing that is the first thing to look at when hunting a wrong match —
    "Fonoteca decided this" and "you decided this" want different amounts of
    suspicion.
  */
  AttributedByPerson: {
    label: 'Matched by you',
    short: 'By you',
    tone: 'neutral',
    note:
      'No rule placed these files. Somebody found the album, checked the pairing track by track ' +
      'and filed them, which is the strongest evidence available for music AcoustID cannot place.',
  },
  NoReleaseByPerson: {
    label: 'No album, said by you',
    short: 'No album',
    tone: 'neutral',
    note:
      'Somebody looked at the candidates and said none of them is the album these files came ' +
      'from — a stronger claim than the rule was in a position to make.',
  },
  Unreleased: {
    label: 'Not from a release',
    short: 'Unreleased',
    tone: 'neutral',
    note:
      "Somebody said this folder is nobody's album — a private recording, a mixtape, tracks " +
      'pulled together by hand — so no release will ever fit it and nothing goes on asking.',
  },

  /*
    The same three, taken by an agent through `/mcp`. Not "by you": you approved
    a tool call, and nobody listened to the files. Warning rather than neutral,
    because that is the suspicion the distinction exists to carry.
  */
  AttributedByAgent: {
    label: 'Matched by an agent',
    short: 'By agent',
    tone: 'warning',
    note:
      'No rule placed these files. An agent chose the album and the pairing and you approved the ' +
      'call, but nobody listened to check it — worth a look before trusting it.',
  },
  NoReleaseByAgent: {
    label: 'No album, said by an agent',
    short: 'No album',
    tone: 'warning',
    note:
      'An agent read the candidates and said none of them is the album these files came from. You ' +
      'approved the call; nobody listened to the files.',
  },
  UnreleasedByAgent: {
    label: 'Not from a release, said by an agent',
    short: 'Unreleased',
    tone: 'warning',
    note:
      "An agent said this folder is nobody's album and you approved the call, so nothing goes on " +
      'asking. Nobody listened to the files.',
  },

  /*
    The four that file nothing. None of them is an error and none is styled as
    one: refusing is a first-class answer here, and a screen that shouted about
    it would be arguing with the rule that produced it. `NotAttempted` is not
    even a refusal — it is a queue position.
  */
  NoConfidentFit: {
    label: 'No release fitted well enough',
    short: 'No fit',
    tone: 'warning',
    note:
      'Releases existed and none of them explained these files well enough to file them. A wrong ' +
      'album is worse than a missing one and far harder to notice, so nothing was written.',
  },
  NoCandidate: {
    label: 'Nothing to compare against',
    short: 'No candidates',
    tone: 'warning',
    note:
      'MusicBrainz holds no release containing these recordings at all, so there was nothing to ' +
      'fit. Live sets, bootlegs and field recordings land here legitimately.',
  },
  LookupFailed: {
    label: 'The lookup did not answer',
    short: 'Lookup failed',
    tone: 'warning',
    note:
      'MusicBrainz did not respond, so no decision was reached. This one is transient — the files ' +
      'stay on the worklist and the next run asks again.',
  },
  NotAttempted: {
    label: 'Not asked yet',
    short: 'Not asked',
    tone: 'neutral',
    note: 'The attribution pass has not reached these files.',
  },
}
