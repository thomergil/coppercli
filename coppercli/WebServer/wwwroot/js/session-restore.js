// Asks the session questions, including the saved height map a file load makes pending. The
// server decides which apply and what each answer does.
import { showConfirm, isConfirmOpen, showError, postJson } from './helpers.js';
import { API_SESSION_RESTORE, PROMPT_SETTLE_MS, TEXT_SESSION_ANSWER_FAILED } from './constants.js';

// Page open, reconnect and file load each start a pass. One arriving mid-pass reruns the pass
// when it ends, because its last read may predate the change.
let asking = false;
let askAgain = false;

/**
 * A question whose answer fails stays pending and is skipped for the rest of this pass. The
 * pass ends if another question replaces its own, or one such as "Abort milling?" is already
 * open, because asking would replace it.
 */
export async function askPendingQuestions() {
    if (asking) {
        askAgain = true;
        return;
    }
    asking = true;

    try {
        let settleMs = 0;
        const failedThisPass = new Set();
        let step;
        while ((step = await nextPendingStep(failedThisPass)) !== null) {
            // Another question can open while the fetch is in flight.
            if (isConfirmOpen()) {
                return;
            }

            const yes = await showConfirm(step.detail, step.question, { settleMs });
            if (yes === null) {
                return;
            }

            const { ok, error } = await postJson(
                API_SESSION_RESTORE, { topic: step.topic, detail: step.detail, yes });
            if (!ok) {
                showError(error || TEXT_SESSION_ANSWER_FAILED);
                failedThisPass.add(step.topic);
            }

            // The next question appears where this answer was tapped.
            settleMs = PROMPT_SETTLE_MS;
        }
    } finally {
        asking = false;
        if (askAgain) {
            askAgain = false;
            askPendingQuestions();
        }
    }
}

/** The first pending question not already failed in this pass, or null. */
async function nextPendingStep(failedThisPass) {
    try {
        const response = await fetch(API_SESSION_RESTORE);
        if (!response.ok) {
            return null;
        }
        const data = await response.json();
        return data.steps?.find(step => !failedThisPass.has(step.topic)) ?? null;
    } catch (err) {
        console.error('Reading the pending session questions failed', err);
        return null;
    }
}
