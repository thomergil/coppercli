// coppercli Web UI Settings Screen

import { $, showError, showInfo } from './helpers.js';
import { showScreen } from './screens.js';
import {
    API_SETTINGS,
    API_PROFILES,
    SCREEN_DASHBOARD,
    TEXT_SETTINGS_SAVED,
    TEXT_UNKNOWN
} from './constants.js';

// Settings field definitions for DRY iteration
const SETTINGS_FIELDS = [
    { id: 'setting-probe-feed', key: 'probeFeed' },
    { id: 'setting-probe-depth', key: 'probeMaxDepth' },
    { id: 'setting-probe-safe', key: 'probeSafeHeight' },
    { id: 'setting-probe-min', key: 'probeMinimumHeight' },
    { id: 'setting-trace-height', key: 'outlineTraceHeight' },
    { id: 'setting-trace-feed', key: 'outlineTraceFeed' },
    { id: 'setting-toolsetter-x', key: 'toolSetterX' },
    { id: 'setting-toolsetter-y', key: 'toolSetterY' }
];

let machineProfiles = [];

export async function loadSettings() {
    try {
        // Load profiles and settings in parallel
        const [profilesRes, settingsRes] = await Promise.all([
            fetch(API_PROFILES),
            fetch(API_SETTINGS)
        ]);
        const [profilesData, settingsData] = await Promise.all([
            profilesRes.json(),
            settingsRes.json()
        ]);

        machineProfiles = profilesData.profiles || [];
        populateProfiles(settingsData.machineProfile);
        populateSettings(settingsData);
    } catch (err) {
        console.error('Failed to load settings:', err);
    }
}

function populateProfiles(selectedProfile) {
    const select = $('setting-machine-profile');
    if (!select) return;

    select.innerHTML = '<option value="">-- None --</option>';
    machineProfiles.forEach(profile => {
        const option = document.createElement('option');
        option.value = profile.id;
        option.textContent = profile.name + (profile.hasToolSetter ? ' (tool setter)' : '');
        if (profile.id === selectedProfile) {
            option.selected = true;
        }
        select.appendChild(option);
    });
}

function populateSettings(data) {
    SETTINGS_FIELDS.forEach(field => {
        const el = $(field.id);
        if (el && data[field.key] !== undefined) {
            el.value = data[field.key];
        }
    });
}

export async function saveSettings() {
    const settings = {};

    // Machine profile
    const profileSelect = $('setting-machine-profile');
    if (profileSelect) {
        settings.machineProfile = profileSelect.value;
    }

    // Numeric fields
    SETTINGS_FIELDS.forEach(field => {
        const el = $(field.id);
        if (el) {
            const value = parseFloat(el.value);
            if (!isNaN(value)) {
                settings[field.key] = value;
            }
        }
    });

    try {
        const response = await fetch(API_SETTINGS, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(settings)
        });
        const data = await response.json();

        if (data.success) {
            showInfo(TEXT_SETTINGS_SAVED);
            showScreen(SCREEN_DASHBOARD);
        } else {
            showError(data.error || TEXT_UNKNOWN);
        }
    } catch (err) {
        showError('Save failed: ' + err.message);
    }
}

export function initSettingsScreen() {
    const saveBtn = $('settings-save-btn');
    const backBtn = $('settings-back-btn');

    if (saveBtn) saveBtn.addEventListener('click', saveSettings);
    if (backBtn) backBtn.addEventListener('click', () => showScreen(SCREEN_DASHBOARD));

    // Load settings when entering screen
    const screen = $('settings-screen');
    if (screen) {
        const observer = new MutationObserver((mutations) => {
            mutations.forEach((mutation) => {
                if (mutation.attributeName === 'class' && screen.classList.contains('active')) {
                    loadSettings();
                }
            });
        });
        observer.observe(screen, { attributes: true });
    }
}
