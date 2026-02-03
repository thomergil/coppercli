// coppercli Web UI File Browser

import { state } from './state.js';
import { $, showError, showInfo, FileBrowser } from './helpers.js';
import { showScreen } from './screens.js';
import {
    API_FILES,
    API_FILE_LOAD,
    API_FILE_UPLOAD,
    SCREEN_DASHBOARD,
    CLASS_HIDDEN,
    TEXT_LOADING,
    TEXT_LOAD,
    TEXT_UNKNOWN,
    TEXT_FILE_UPLOADED,
    BYTES_PER_KB,
    BYTES_PER_MB,
    POSITION_DECIMALS_BRIEF
} from './constants.js';

// Shared file browser instance
let fileBrowser = null;

function formatSize(bytes) {
    if (bytes < BYTES_PER_KB) return bytes + ' B';
    if (bytes < BYTES_PER_MB) return (bytes / BYTES_PER_KB).toFixed(POSITION_DECIMALS_BRIEF) + ' KB';
    return (bytes / BYTES_PER_MB).toFixed(POSITION_DECIMALS_BRIEF) + ' MB';
}

function onFileSelect(path) {
    state.selectedFile = path;
    // Show file info panel
    document.getElementById('selected-file-name').textContent = path.split(/[/\\]/).pop();
    document.getElementById('file-info').classList.remove(CLASS_HIDDEN);
    // Clear details (we don't have preview API)
    document.getElementById('file-lines').textContent = '';
    document.getElementById('file-time').textContent = '';
    document.getElementById('file-bounds').textContent = '';
}

function onFileLoad(path) {
    state.selectedFile = path;
    loadFile();
}

export async function loadFiles(path) {
    if (!fileBrowser) {
        fileBrowser = new FileBrowser({
            listElementId: 'file-list',
            pathElementId: 'current-path',
            apiEndpoint: API_FILES,
            fileIcon: '📄',
            metaField: 'size',
            formatMeta: formatSize,
            onFileSelect: onFileSelect,
            onFileLoad: onFileLoad
        });
    }
    await fileBrowser.load(path);
    // Hide file info until selection
    document.getElementById('file-info').classList.add(CLASS_HIDDEN);
    state.selectedFile = null;
}

export async function loadFile() {
    if (!state.selectedFile) return;

    const btn = document.getElementById('load-file-btn');
    btn.disabled = true;
    btn.textContent = TEXT_LOADING;

    try {
        const response = await fetch(API_FILE_LOAD, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: state.selectedFile })
        });

        const data = await response.json();

        if (data.success) {
            showInfo(`Loaded: ${data.name} (${data.lines} lines)`);
            showScreen(SCREEN_DASHBOARD);
        } else {
            showError('Failed to load file: ' + (data.error || TEXT_UNKNOWN));
        }
    } catch (err) {
        showError('Failed to load file: ' + err.message);
    } finally {
        btn.disabled = false;
        btn.textContent = TEXT_LOAD;
    }
}

async function uploadFile(file) {
    const formData = new FormData();
    formData.append('file', file);

    const uploadBtn = $('upload-file-btn');
    if (uploadBtn) {
        uploadBtn.disabled = true;
        uploadBtn.textContent = 'Uploading...';
    }

    try {
        const response = await fetch(API_FILE_UPLOAD, {
            method: 'POST',
            body: formData
        });
        const data = await response.json();

        if (data.success) {
            showInfo(`${TEXT_FILE_UPLOADED}: ${data.name} (${data.lines} lines)`);
            showScreen(SCREEN_DASHBOARD);
        } else {
            showError(data.error || TEXT_UNKNOWN);
        }
    } catch (err) {
        showError('Upload failed: ' + err.message);
    } finally {
        if (uploadBtn) {
            uploadBtn.disabled = false;
            uploadBtn.textContent = 'Upload';
        }
    }
}

function handleFileInputChange(event) {
    const file = event.target.files?.[0];
    if (file) {
        uploadFile(file);
    }
    // Reset input so same file can be selected again
    event.target.value = '';
}

export function initFileScreen() {
    $('load-file-btn').addEventListener('click', loadFile);

    // Upload button triggers hidden file input
    const uploadBtn = $('upload-file-btn');
    const fileInput = $('file-upload-input');
    if (uploadBtn && fileInput) {
        uploadBtn.addEventListener('click', () => fileInput.click());
        fileInput.addEventListener('change', handleFileInputChange);
    }
}
