/**
 * An undo of an album folder, in English: what the confirmation asks and what
 * the result says.
 *
 * One press steps the folder back by one edit — a tag write's tags, renames,
 * links and sleeve together, or identification's AcoustID — so the question
 * names which one, and when, before anything is changed.
 *
 * Nothing here imports React, a stylesheet or the API client at runtime, so it
 * runs under `node --test`.
 */

import type { components } from '@fonoteca/api-client'

type Edit = components['schemas']['FolderEdit']
type Result = components['schemas']['TagUndoResult']

/** The button's label for the edit it would reverse. */
export function undoLabel(edit: Edit): string {
  return edit.kind === 'TagWrite' ? 'Undo the last tag write' : 'Undo the AcoustID tag'
}

/** The confirmation, naming the edit, its date and what follows from undoing it. */
export function undoQuestion(folder: string, edit: Edit): string {
  const when = new Date(edit.at).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
  const files = `${edit.files} file${edit.files === 1 ? '' : 's'}`

  return edit.kind === 'TagWrite'
    ? `Undo the tag write of ${when} on “${folder}”?\n\n` +
        `The tags it wrote into ${files} go back to what they were, even where another tagger ` +
        'has changed them since, and the files and folder it renamed get their old names back. ' +
        'The folder then becomes a question on the Identify screen, so the next tag write ' +
        'leaves it alone until you answer it.'
    : `Take out the AcoustID that identification wrote on ${when} into ${files} in “${folder}”?\n\n` +
        'The catalogue keeps the AcoustID; only the tag in the files goes.'
}

/** What the undo did, in one sentence, with what it had to leave as it was. */
export function undoOutcome(result: Result): string {
  if (result.status === 'Incomplete') {
    const back =
      result.restored > 0 ? ` ${count(result.restored, 'file')} already got their tags back;` : ''

    return (
      `Not finished: ${result.problems.join('; ')}.${back} nothing was moved or reopened, ` +
      'and the edit is still there to undo once the file can be written.'
    )
  }

  const done = [
    result.restored > 0 ? `${count(result.restored, 'file')} got their tags back` : null,
    result.moved > 0 ? `${count(result.moved, 'move')} reversed` : null,
    result.unlinked > 0 ? `${count(result.unlinked, 'link')} removed` : null,
    result.relinked > 0 ? `${count(result.relinked, 'link')} put back` : null,
    result.covers > 0 ? 'the sleeve put back' : null,
    result.reopened > 0 ? `${count(result.reopened, 'file')} reopened as a question` : null,
  ].filter((part) => part !== null)

  const said = done.length === 0 ? 'Nothing needed changing.' : `${capitalise(done.join(', '))}.`

  return result.problems.length === 0
    ? said
    : `${said} Left as they were: ${result.problems.join('; ')}.`
}

function count(value: number, noun: string): string {
  return `${value} ${noun}${value === 1 ? '' : 's'}`
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1)
}
