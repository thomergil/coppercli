// coppercli Web UI - Trust Work Zero Modal

import { $, showInfo } from './helpers.js';
import { API_STATUS, API_TRUST_WORK_ZERO, CLASS_HIDDEN, TEXT_WORK_ZERO_TRUSTED } from './constants.js';

export function initTrustZeroModal() {
    const yesBtn = $('trust-zero-yes-btn');
    const noBtn = $('trust-zero-no-btn');

    if (yesBtn) {
        yesBtn.addEventListener('click', async () => {
            hideTrustZeroModal();
            try {
                const response = await fetch(API_TRUST_WORK_ZERO, { method: 'POST' });
                const data = await response.json();
                if (data.success) {
                    showInfo(TEXT_WORK_ZERO_TRUSTED);
                }
            } catch (err) {
                console.error('Failed to trust work zero:', err);
            }
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
    // console.log('checkAndShowTrustZero called');
    try {
        const response = await fetch(API_STATUS);
        const status = await response.json();
        // console.log('checkAndShowTrustZero status:', status.hasStoredWorkZero, status.isWorkZeroSet, status.milling);

        // Show modal if: connected AND stored work zero exists AND not yet trusted AND not milling
        if (status.connected && status.hasStoredWorkZero && !status.isWorkZeroSet && !status.milling) {
            // console.log('checkAndShowTrustZero: showing modal');
            showTrustZeroModal();
        }
    } catch (err) {
        console.error('Failed to check trust zero status:', err);
    }
}
