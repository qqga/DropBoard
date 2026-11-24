const currentRoomId = getRoomIdFromUrl();
document.getElementById('roomInput').value = currentRoomId;

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/board")
    .withAutomaticReconnect()
    .build();

function getRoomIdFromUrl() {
    const path = window.location.pathname.replace(/^\/+|\/+$/g, '');
    if (/^[a-zA-Z0-9_-]{3,32}$/.test(path)) return path;
    const randomId = Math.random().toString(36).substring(2, 8);
    window.history.replaceState({}, '', '/' + randomId);
    return randomId;
}

function switchRoom() {
    const val = document.getElementById('roomInput').value.trim();
    if (!/^[a-zA-Z0-9_-]{3,32}$/.test(val)) {
        showError('ID комнаты должен содержать 3–32 символа (латиница, цифры, - или _)');
        return;
    }
    window.location.href = '/' + val;
}

// Обработчики кнопок и инпутов
document.getElementById('switchRoomBtn').addEventListener('click', switchRoom);
document.getElementById('roomInput').addEventListener('keydown', e => {
    if (e.key === 'Enter') switchRoom();
});

document.getElementById('copyUrlBtn').addEventListener('click', copyRoomUrl);
document.getElementById('qrBtn').addEventListener('click', toggleQrModal);
document.getElementById('qrModal').addEventListener('click', toggleQrModal);
document.getElementById('qrModalCard').addEventListener('click', e => e.stopPropagation());
document.getElementById('closeQrBtn').addEventListener('click', toggleQrModal);

document.getElementById('sendBtn').addEventListener('click', sendText);
document.getElementById('fileInput').addEventListener('change', e => handleFileSelect(e.target.files));

document.getElementById('textInput').addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') sendText();
});

// SignalR события
connection.on("ItemAdded", item => {
    renderItem(item, true);
    updateEmptyState();
});

connection.on("ItemBurned", itemId => {
    const el = document.getElementById('item-' + itemId);
    if (el) {
        el.innerHTML = '<div class="text-xs text-rose-400 italic py-1">🔥 Секрет был просмотрен и самоуничтожился.</div>';
        setTimeout(() => {
            el.remove();
            updateEmptyState();
        }, 4000);
    }
});

connection.on("ItemRemoved", itemId => {
    const el = document.getElementById('item-' + itemId);
    if (el) {
        el.remove();
        updateEmptyState();
    }
});

connection.onreconnecting(() => setStatus('Переподключение...', 'amber'));
connection.onreconnected(() => joinCurrentRoom());
connection.onclose(() => setStatus('Отключено', 'rose'));

async function start() {
    try {
        await connection.start();
        await joinCurrentRoom();
    } catch (err) {
        setStatus('Ошибка связи', 'rose');
        setTimeout(start, 3000);
    }
}

async function joinCurrentRoom() {
    setStatus('Онлайн', 'emerald');
    const state = await connection.invoke("JoinRoom", currentRoomId);
    const container = document.getElementById('itemsContainer');
    container.innerHTML = '';
    state.items.forEach(item => renderItem(item, false));
    updateEmptyState();
}

async function sendText() {
    const input = document.getElementById('textInput');
    const text = input.value;
    if (!text.trim()) return;

    const isBurn = document.getElementById('burnCheckbox').checked;
    try {
        clearError();
        await connection.invoke("SendText", currentRoomId, text, isBurn);
        input.value = '';
        document.getElementById('burnCheckbox').checked = false;
    } catch (err) {
        showError(err.message || 'Ошибка отправки');
    }
}

async function handleFileSelect(files) {
    if (!files || files.length === 0) return;
    const isBurn = document.getElementById('burnCheckbox').checked;
    const progress = document.getElementById('uploadProgress');
    clearError();

    for (const file of files) {
        progress.classList.remove('hidden');
        progress.textContent = 'Загрузка: ' + file.name + '...';

        const formData = new FormData();
        formData.append('file', file);
        formData.append('burn', isBurn);

        try {
            const res = await fetch('/api/rooms/' + encodeURIComponent(currentRoomId) + '/files', {
                method: 'POST',
                body: formData
            });
            if (!res.ok) {
                const data = await res.json().catch(() => ({}));
                throw new Error(data.error || 'Ошибка загрузки (' + res.status + ')');
            }
        } catch (err) {
            showError(err.message);
        }
    }

    progress.classList.add('hidden');
    document.getElementById('fileInput').value = '';
    document.getElementById('burnCheckbox').checked = false;
}

// Вставка картинок/файлов из буфера (Ctrl+V)
window.addEventListener('paste', e => {
    if (e.clipboardData && e.clipboardData.files.length > 0) {
        e.preventDefault();
        handleFileSelect(e.clipboardData.files);
    }
});

// Drag & Drop
const dropZone = document.getElementById('dropZone');
['dragenter', 'dragover'].forEach(evt => {
    dropZone.addEventListener(evt, e => {
        e.preventDefault();
        dropZone.classList.add('drag-active');
    });
});
['dragleave', 'drop'].forEach(evt => {
    dropZone.addEventListener(evt, e => {
        e.preventDefault();
        dropZone.classList.remove('drag-active');
    });
});
dropZone.addEventListener('drop', e => {
    if (e.dataTransfer.files.length > 0) {
        handleFileSelect(e.dataTransfer.files);
    }
});

function renderItem(item, prepend) {
    if (document.getElementById('item-' + item.id)) return;

    const card = document.createElement('div');
    card.id = 'item-' + item.id;
    card.className = 'bg-slate-900 border border-slate-800 rounded-xl p-4 shadow flex flex-col gap-2 transition';

    const timeStr = new Date(item.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    const header = document.createElement('div');
    header.className = 'flex items-center justify-between text-xs text-slate-400';

    const meta = document.createElement('div');
    meta.className = 'flex items-center gap-2';
    meta.innerHTML = '<span>' + (item.type === 'file' ? '📄 Файл' : '📝 Текст') + '</span><span>• ' + timeStr + '</span>';

    if (item.isBurnAfterRead) {
        const badge = document.createElement('span');
        badge.className = 'text-xs bg-rose-500/20 text-rose-300 px-2 py-0.5 rounded';
        badge.textContent = '🔥 Одноразовый';
        meta.appendChild(badge);
    }

    const delBtn = document.createElement('button');
    delBtn.className = 'text-slate-500 hover:text-rose-400 transition';
    delBtn.title = 'Удалить';
    delBtn.textContent = '✕';
    delBtn.addEventListener('click', () => deleteItem(item.id));

    header.appendChild(meta);
    header.appendChild(delBtn);
    card.appendChild(header);

    if (item.type === 'text') {
        if (item.isBurnAfterRead) {
            const secretBox = document.createElement('div');
            secretBox.id = 'secret-box-' + item.id;

            const revealBtn = document.createElement('button');
            revealBtn.className = 'mt-1 bg-rose-600 hover:bg-rose-500 text-white text-xs font-medium px-3 py-2 rounded-lg transition';
            revealBtn.textContent = '👁 Показать секретный текст (удалится после просмотра)';
            revealBtn.addEventListener('click', () => revealSecret(item.id));

            secretBox.appendChild(revealBtn);
            card.appendChild(secretBox);
        } else {
            const pre = document.createElement('pre');
            pre.className = 'whitespace-pre-wrap break-words text-sm text-slate-100 bg-slate-950/70 p-3 rounded-lg border border-slate-800/80 font-mono';
            pre.textContent = item.content;

            const copyBtn = document.createElement('button');
            copyBtn.className = 'self-start text-xs bg-slate-800 hover:bg-slate-700 text-slate-200 px-3 py-1.5 rounded-lg transition';
            copyBtn.textContent = '📋 Копировать';
            copyBtn.addEventListener('click', () => {
                navigator.clipboard.writeText(item.content);
                copyBtn.textContent = '✅ Скопировано!';
                setTimeout(() => copyBtn.textContent = '📋 Копировать', 1500);
            });

            card.appendChild(pre);
            card.appendChild(copyBtn);
        }
    } else if (item.type === 'file') {
        const sizeMb = (item.fileSize / (1024 * 1024)).toFixed(2);
        const fileRow = document.createElement('div');
        fileRow.className = 'flex items-center justify-between bg-slate-950/70 p-3 rounded-lg border border-slate-800/80 gap-3';

        const info = document.createElement('div');
        info.className = 'truncate';
        const nameEl = document.createElement('div');
        nameEl.className = 'text-sm font-medium text-slate-200 truncate';
        nameEl.textContent = item.fileName;
        const sizeEl = document.createElement('div');
        sizeEl.className = 'text-xs text-slate-500';
        sizeEl.textContent = sizeMb + ' МБ';
        info.appendChild(nameEl);
        info.appendChild(sizeEl);

        const dlLink = document.createElement('a');
        dlLink.href = '/api/rooms/' + encodeURIComponent(currentRoomId) + '/files/' + encodeURIComponent(item.id);
        dlLink.className = 'shrink-0 text-xs bg-sky-500/20 hover:bg-sky-500/30 text-sky-300 border border-sky-500/30 px-3 py-2 rounded-lg transition font-medium';
        dlLink.textContent = item.isBurnAfterRead ? '🔥 Скачать и сжечь' : '⬇ Скачать';

        fileRow.appendChild(info);
        fileRow.appendChild(dlLink);
        card.appendChild(fileRow);
    }

    const container = document.getElementById('itemsContainer');
    if (prepend) container.prepend(card);
    else container.appendChild(card);
}

async function revealSecret(itemId) {
    const secret = await connection.invoke("RevealSecret", currentRoomId, itemId);
    const box = document.getElementById('secret-box-' + itemId);
    if (!box) return;
    if (secret === null) {
        box.innerHTML = 'Секрет уже был прочитан кем-то другим.';
        return;
    }
    box.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'whitespace-pre-wrap break-words text-sm text-amber-200 bg-slate-950 p-3 rounded-lg border border-rose-500/40 font-mono mb-2';
    pre.textContent = secret;
    const copyBtn = document.createElement('button');
    copyBtn.className = 'text-xs bg-slate-800 hover:bg-slate-700 text-slate-200 px-3 py-1.5 rounded-lg';
    copyBtn.textContent = '📋 Скопировать секрет (он уже удалён с сервера)';
    copyBtn.addEventListener('click', () => {
        navigator.clipboard.writeText(secret);
        copyBtn.textContent = '✅ Скопировано!';
    });
    box.appendChild(pre);
    box.appendChild(copyBtn);
}
async function deleteItem(itemId) {
    await connection.invoke("DeleteItem", currentRoomId, itemId);
}
function copyRoomUrl() {
    navigator.clipboard.writeText(window.location.href);
    setStatus('Ссылка скопирована!', 'sky');
    setTimeout(() => setStatus('Онлайн', 'emerald'), 1500);
}
let qrGenerated = false;
function toggleQrModal() {
    const modal = document.getElementById('qrModal');
    modal.classList.toggle('hidden');
    if (!qrGenerated && !modal.classList.contains('hidden')) {
        new QRCode(document.getElementById("qrcode"), {
            text: window.location.href,
            width: 200,
            height: 200
        });
        qrGenerated = true;
    }
}
function setStatus(text, color) {
    const badge = document.getElementById('statusBadge');
    badge.textContent = text;
    badge.className = 'text-xs px-2 py-0.5 rounded-full bg-' + color + '-500/20 text-' + color + '-300';
}
function updateEmptyState() {
    const hasItems = document.getElementById('itemsContainer').children.length > 0;
    document.getElementById('emptyState').classList.toggle('hidden', hasItems);
}
function showError(msg) {
    const el = document.getElementById('errorBanner');
    el.textContent = msg;
    el.classList.remove('hidden');
}
function clearError() {
    document.getElementById('errorBanner').classList.add('hidden');
}
start();