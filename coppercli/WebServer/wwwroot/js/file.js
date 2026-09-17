// coppercli Web UI File Browser

import { state } from './state.js';
import { $, showError, showInfo, FileBrowser, format, whileBusy } from './helpers.js';
import { showScreen } from './screens.js';
import {
    API_FILES,
    API_FILE_LOAD,
    API_FILE_UPLOAD,
    SCREEN_DASHBOARD,
    CLASS_HIDDEN,
    TEXT_LOADING,
    TEXT_FILE_UPLOADED,
    BYTES_PER_KB,
    BYTES_PER_MB,
    TEXT_UPLOADING,
    TEXT_FILE_LOAD_FAILED,
    TEXT_UPLOAD_FAILED,
    TEXT_FILE_LOADED,
    TEXT_HEIGHT_MAP_DROPPED,
    FILE_SIZE_DECIMALS,
    TEXT_SIZE_BYTES,
    TEXT_SIZE_KB,
    TEXT_SIZE_MB,
} from './constants.js';

// Shared file browser instance
let fileBrowser = null;

function formatSize(bytes) {
    if (bytes < BYTES_PER_KB) {
        return format(TEXT_SIZE_BYTES, bytes);
    }
    if (bytes < BYTES_PER_MB) {
        return format(TEXT_SIZE_KB, (bytes / BYTES_PER_KB).toFixed(FILE_SIZE_DECIMALS));
    }
    return format(TEXT_SIZE_MB, (bytes / BYTES_PER_MB).toFixed(FILE_SIZE_DECIMALS));
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

    const done = whileBusy(document.getElementById('load-file-btn'), TEXT_LOADING);

    try {
        const response = await fetch(API_FILE_LOAD, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: state.selectedFile })
        });

        const data = await response.json();

        if (data.success) {
            showInfo(format(TEXT_FILE_LOADED, data.name, data.lines));
            reportDroppedMap(data);
            showScreen(SCREEN_DASHBOARD);
        } else {
            showError(data.error || TEXT_FILE_LOAD_FAILED);
        }
    } catch (err) {
        console.error('file load failed', err);
        showError(TEXT_FILE_LOAD_FAILED);
    } finally {
        done();
    }
}

// Loading a file can drop the height map that was in hand. The terminal says so; without
// this the browser operator watches it vanish from the probe panel with no reason given.
function reportDroppedMap(data) {
    if (data.droppedMap) {
        showError(format(TEXT_HEIGHT_MAP_DROPPED, data.droppedMap));
    }
}

async function uploadFile(file) {
    const formData = new FormData();
    formData.append('file', file);

    const done = whileBusy($('upload-file-btn'), TEXT_UPLOADING);

    try {
        const response = await fetch(API_FILE_UPLOAD, {
            method: 'POST',
            body: formData
        });
        const data = await response.json();

        if (data.success) {
            showInfo(format(TEXT_FILE_UPLOADED, data.name, data.lines));
            reportDroppedMap(data);
            showScreen(SCREEN_DASHBOARD);
        } else {
            showError(data.error || TEXT_UPLOAD_FAILED);
        }
    } catch (err) {
        console.error('upload failed', err);
        showError(TEXT_UPLOAD_FAILED);
    } finally {
        done();
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
