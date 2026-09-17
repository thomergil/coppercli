import { $, showInfo, showError, postJson } from './helpers.js';
import {
    API_STATUS,
    API_TRUST_WORK_ZERO,
    CLASS_HIDDEN,
    TEXT_WORK_ZERO_TRUSTED,
    TEXT_WORK_ZERO_NOT_TRUSTED
} from './constants.js';

export function initTrustZeroModal() {
    const yesBtn = $('trust-zero-yes-btn');
    const noBtn = $('trust-zero-no-btn');

    if (yesBtn) {
        yesBtn.addEventListener('click', async () => {
            // The modal stays up unless the server confirms the work zero was trusted. A
            // refusal the operator does not see would leave them trusting a zero nobody set.
            const result = await postJson(API_TRUST_WORK_ZERO);
            if (!result.ok) {
                showError(result.error || TEXT_WORK_ZERO_NOT_TRUSTED);
                return;
            }

            hideTrustZeroModal();
            showInfo(TEXT_WORK_ZERO_TRUSTED);
        });
    }

    if (noBtn) {
        noBtn.addEventListener('click', () => {
            hideTrustZeroModal();
        });
    }
}

function showTrustZeroModal() {
    const modal = $('trust-zero-modal');
    if (modal) modal.classList.remove(CLASS_HIDDEN);
}

function hideTrustZeroModal() {
    const modal = $('trust-zero-modal');
    if (modal) modal.classList.add(CLASS_HIDDEN);
}

export async function checkAndShowTrustZero() {
    try {
        const response = await fetch(API_STATUS);
        const status = await response.json();

        if (status.connected && status.hasStoredWorkZero && !status.isWorkZeroSet && !status.milling) {
            showTrustZeroModal();
        }
    } catch (err) {
        console.error('Failed to check trust zero status:', err);
    }
}
