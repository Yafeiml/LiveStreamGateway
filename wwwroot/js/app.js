// ==========================================================
// LiveStreamGateway - Web Management Controller
// ==========================================================

let appState = {
    status: null,
    config: null,
    cookieStatuses: {},
    cookieEditors: { huya: false, douyu: false, bilibili: false },
    editingChannelId: null,
    hlsPlayer: null,
    hlsRetryTimer: null,
    hlsStallTimer: null,
    previewToken: 0,
    previewUrl: null,
    previewRecoveryAttempts: 0,
    previewMediaRecoveries: 0,
    previewHardRecoveries: 0,
    previewLastHardRecoveryAt: 0,
    previewLastMediaRecoveryAt: 0,
    previewLastSoftRecoveryAt: 0,
    previewSevereErrorCount: 0,
    previewLastSevereErrorAt: 0,
    previewLastProgressAt: 0,
    previewLastTime: 0,
    previewBufferedFragments: 0,
    previewLastFragEndPts: null,
    previewLastFragSequence: null,
    previewLastFragContinuity: null,
    previewAudioCodecSignature: null,
    previewRecoveryExhausted: false,
    pollTimer: null,
    urlDebounceTimer: null,
    authenticated: false,
    setupRequired: false,
    managementStarted: false,
    handlingUnauthorized: false
};

const PREVIEW_MAX_RELOAD_ATTEMPTS = 6;
const PREVIEW_MAX_MEDIA_RECOVERIES = 2;
const PREVIEW_MAX_HARD_RECOVERIES = 3;
const PREVIEW_HARD_RECOVERY_COOLDOWN_MS = 8000;
const PREVIEW_MEDIA_RECOVERY_COOLDOWN_MS = 5000;
const PREVIEW_SOFT_STALL_RECOVERY_MS = 6000;
const PREVIEW_HARD_STALL_RECOVERY_MS = 12000;
const PREVIEW_SEVERE_ERROR_WINDOW_MS = 10000;
const PREVIEW_TIMESTAMP_GAP_SECONDS = 1.5;

document.addEventListener('DOMContentLoaded', () => {
    initApp();
});

async function initApp() {
    setupEventListeners();
    await initializeAuthentication();
}

function setupEventListeners() {
    document.getElementById('form-login')?.addEventListener('submit', submitAuthentication);
    document.getElementById('btn-toggle-login-password')?.addEventListener('click', () => {
        const password = document.getElementById('login-password');
        setPasswordVisibility('login-password', 'btn-toggle-login-password', password?.type === 'password');
    });
    document.getElementById('btn-toggle-confirm-password')?.addEventListener('click', () => {
        const password = document.getElementById('auth-confirm-password');
        setPasswordVisibility('auth-confirm-password', 'btn-toggle-confirm-password', password?.type === 'password');
    });
    document.getElementById('btn-logout')?.addEventListener('click', logoutAdmin);
    document.getElementById('btn-playback-security')?.addEventListener('click', openPlaybackSecurityModal);
    document.getElementById('btn-copy-secure-m3u')?.addEventListener('click', () => {
        const url = document.getElementById('playback-m3u-url')?.value || '';
        if (url) copyTextToClipboard(url, 'M3U 地址', document.getElementById('btn-copy-secure-m3u'));
    });
    document.getElementById('playback-token-auth-enabled')?.addEventListener('change', updatePlaybackTokenAuthentication);
    document.getElementById('form-rotate-playback-token')?.addEventListener('submit', rotatePlaybackToken);
    document.getElementById('btn-change-password')?.addEventListener('click', () => {
        document.getElementById('form-change-password')?.reset();
        const error = document.getElementById('password-change-error');
        if (error) error.innerText = '';
        openModal('modal-password');
        setTimeout(() => document.getElementById('current-admin-password')?.focus(), 50);
    });
    document.getElementById('form-change-password')?.addEventListener('submit', changeAdminPassword);

    // Copy M3U Button
    document.getElementById('btn-copy-m3u')?.addEventListener('click', copyM3uUrl);
    
    // Manual Refresh Button with Click Spin Animation
    document.getElementById('btn-manual-refresh')?.addEventListener('click', async () => {
        const btn = document.getElementById('btn-manual-refresh');
        btn?.classList.add('spinning');
        await loadStatus(true);
        setTimeout(() => {
            btn?.classList.remove('spinning');
        }, 600);
    });

    // Add Channel Button
    document.getElementById('btn-add-channel')?.addEventListener('click', () => {
        openAddChannelModal();
    });

    // Manage Cookies Button
    document.getElementById('btn-manage-cookies')?.addEventListener('click', () => {
        openCookieModal();
    });

    // Copy stream url button inside preview modal
    document.getElementById('btn-copy-stream-url')?.addEventListener('click', () => {
        const url = document.getElementById('preview-hls-url')?.innerText;
        const btn = document.getElementById('btn-copy-stream-url');
        if (url) {
            copyTextToClipboard(url, 'HLS 播放链接', btn);
        }
    });
}

// -------------------------------------------------------------
// Management Authentication
// -------------------------------------------------------------

async function initializeAuthentication() {
    try {
        const res = await window.fetch('/api/auth/session', {
            credentials: 'same-origin',
            cache: 'no-store'
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);

        const session = await res.json();
        const transportWarning = document.getElementById('auth-transport-warning');
        setAuthenticationMode(session);
        if (transportWarning) transportWarning.hidden = session.secureTransport === true;

        if (session.authenticated) {
            unlockManagement();
            await startManagementApp();
            return;
        }

        lockManagement();
    } catch (err) {
        setAuthenticationMode({ setupRequired: false });
        lockManagement(`无法连接管理服务：${err.message}`);
    }
}

async function startManagementApp() {
    appState.authenticated = true;
    await Promise.all([loadConfig(), loadStatus()]);

    if (appState.pollTimer) window.clearInterval(appState.pollTimer);
    appState.pollTimer = window.setInterval(loadStatus, 3000);
    appState.managementStarted = true;
}

function unlockManagement() {
    appState.authenticated = true;
    appState.handlingUnauthorized = false;
    document.getElementById('auth-gate')?.classList.add('authenticated');
    document.getElementById('app-shell')?.removeAttribute('inert');
    const loginError = document.getElementById('login-error');
    if (loginError) loginError.innerText = '';
}

function lockManagement(message = '') {
    appState.authenticated = false;
    appState.managementStarted = false;
    if (appState.pollTimer) window.clearInterval(appState.pollTimer);
    appState.pollTimer = null;
    appState.previewToken++;
    resetPreviewPlayback();

    document.querySelectorAll('.modal-backdrop.active').forEach(modal => modal.classList.remove('active'));
    document.getElementById('auth-gate')?.classList.remove('authenticated');
    document.getElementById('app-shell')?.setAttribute('inert', '');
    const loginError = document.getElementById('login-error');
    if (loginError) loginError.innerText = message;
    const password = document.getElementById('login-password');
    if (password) password.value = '';
    const confirmPassword = document.getElementById('auth-confirm-password');
    if (confirmPassword) confirmPassword.value = '';
    setPasswordVisibility('login-password', 'btn-toggle-login-password', false, false);
    setPasswordVisibility('auth-confirm-password', 'btn-toggle-confirm-password', false, false);
    setAuthSubmitIdleState();
    window.setTimeout(() => password?.focus(), 50);
}

function setAuthenticationMode(session) {
    const setupRequired = session?.setupRequired === true;
    appState.setupRequired = setupRequired;

    const card = document.querySelector('.auth-card');
    const form = document.getElementById('form-login');
    const subtitle = document.getElementById('auth-subtitle');
    const setupMessage = document.getElementById('auth-setup-message');
    const setupMessageDetail = document.getElementById('auth-setup-message-detail');
    const password = document.getElementById('login-password');
    const confirmGroup = document.getElementById('auth-confirm-password-group');
    const confirmPassword = document.getElementById('auth-confirm-password');
    const passwordHint = document.getElementById('auth-password-hint');

    card?.classList.toggle('setup-mode', setupRequired);
    if (form) form.hidden = false;
    if (subtitle) subtitle.innerText = setupRequired ? '首次使用 · 创建管理员密码' : '登录管理控制台';
    if (setupMessage) setupMessage.hidden = !setupRequired;
    if (setupMessageDetail) {
        setupMessageDetail.innerText = '检测到尚未创建管理员密码。完成设置后将自动登录，并生成独立的 Jellyfin / IPTV 播放令牌。';
    }
    if (confirmGroup) confirmGroup.hidden = !setupRequired;
    if (confirmPassword) {
        confirmPassword.disabled = !setupRequired;
        confirmPassword.required = setupRequired;
    }
    if (passwordHint) passwordHint.hidden = !setupRequired;
    if (password) {
        password.autocomplete = setupRequired ? 'new-password' : 'current-password';
        password.placeholder = setupRequired ? '创建管理员密码' : '请输入管理员密码';
        password.setAttribute('aria-label', setupRequired ? '创建管理员密码' : '管理员密码');
        if (setupRequired) password.setAttribute('minlength', '12');
        else password.removeAttribute('minlength');
    }

    if (password) password.value = '';
    if (confirmPassword) confirmPassword.value = '';
    setPasswordVisibility('login-password', 'btn-toggle-login-password', false, false);
    setPasswordVisibility('auth-confirm-password', 'btn-toggle-confirm-password', false, false);
    setAuthSubmitIdleState();
}

function setPasswordVisibility(inputId, toggleId, visible, focus = true) {
    const password = document.getElementById(inputId);
    const toggle = document.getElementById(toggleId);
    if (!password || !toggle) return;

    password.type = visible ? 'text' : 'password';
    toggle.setAttribute('aria-pressed', String(visible));
    const confirmField = inputId === 'auth-confirm-password';
    const label = confirmField ? '确认密码' : '密码';
    toggle.setAttribute('aria-label', visible ? `隐藏${label}` : `显示${label}`);
    toggle.title = visible ? `隐藏${label}` : `显示${label}`;
    if (focus) password.focus({ preventScroll: true });

    const cursorPosition = password.value.length;
    password.setSelectionRange?.(cursorPosition, cursorPosition);
}

function setAuthSubmitIdleState() {
    const button = document.getElementById('btn-login');
    if (!button) return;
    button.disabled = false;
    button.innerText = appState.setupRequired ? '创建密码并进入控制台' : '登录';
}

async function apiFetch(input, options = {}) {
    const headers = new Headers(options.headers || {});
    const method = String(options.method || 'GET').toUpperCase();
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method))
        headers.set('X-LSG-Request', '1');

    const res = await window.fetch(input, {
        ...options,
        method,
        headers,
        credentials: 'same-origin',
        cache: options.cache || 'no-store'
    });

    if (res.status === 401 && !appState.handlingUnauthorized) {
        appState.handlingUnauthorized = true;
        lockManagement('登录会话已过期，请重新登录');
    }
    return res;
}

async function submitAuthentication(event) {
    event.preventDefault();
    if (appState.setupRequired) await setupAdministrator();
    else await loginAdmin();
}

async function setupAdministrator() {
    const passwordInput = document.getElementById('login-password');
    const confirmPasswordInput = document.getElementById('auth-confirm-password');
    const button = document.getElementById('btn-login');
    const error = document.getElementById('login-error');
    const password = passwordInput?.value || '';
    const confirmedPassword = confirmPasswordInput?.value || '';

    if (error) error.innerText = '';
    if (password.length < 12) {
        if (error) error.innerText = '管理员密码至少需要 12 个字符';
        passwordInput?.focus();
        return;
    }
    if (password !== confirmedPassword) {
        if (error) error.innerText = '两次输入的管理员密码不一致';
        confirmPasswordInput?.focus();
        return;
    }

    if (button) {
        button.disabled = true;
        button.innerText = '正在创建...';
    }

    try {
        const res = await window.fetch('/api/auth/setup', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-LSG-Request': '1'
            },
            credentials: 'same-origin',
            cache: 'no-store',
            body: JSON.stringify({ password })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            if (res.status === 409) {
                await initializeAuthentication();
                const refreshedError = document.getElementById('login-error');
                if (refreshedError) refreshedError.innerText = '管理员密码已经由其他会话完成设置，请使用已设置的密码登录';
                return;
            }
            if (error) error.innerText = data.error || '管理员密码创建失败';
            return;
        }

        if (passwordInput) passwordInput.value = '';
        if (confirmPasswordInput) confirmPasswordInput.value = '';
        setAuthenticationMode({ setupRequired: false });
        unlockManagement();
        await startManagementApp();
        showToast('管理员密码已创建，已安全进入控制台', 'success');
    } catch (err) {
        if (error) error.innerText = `首次设置请求失败：${err.message}`;
    } finally {
        setAuthSubmitIdleState();
    }
}

async function loginAdmin() {
    const passwordInput = document.getElementById('login-password');
    const button = document.getElementById('btn-login');
    const error = document.getElementById('login-error');
    const password = passwordInput?.value || '';

    if (button) {
        button.disabled = true;
        button.innerText = '登录中...';
    }
    if (error) error.innerText = '';

    try {
        const res = await window.fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            cache: 'no-store',
            body: JSON.stringify({ password })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            if (res.status === 503) {
                await initializeAuthentication();
                return;
            }
            if (error) error.innerText = data.error || '登录失败';
            return;
        }

        if (passwordInput) passwordInput.value = '';
        unlockManagement();
        await startManagementApp();
    } catch (err) {
        if (error) error.innerText = `登录请求失败：${err.message}`;
    } finally {
        setAuthSubmitIdleState();
    }
}

async function logoutAdmin() {
    try {
        await apiFetch('/api/auth/logout', { method: 'POST' });
    } catch (_) { }
    lockManagement('已安全退出');
}

async function changeAdminPassword(event) {
    event.preventDefault();
    const currentPassword = document.getElementById('current-admin-password')?.value || '';
    const newPassword = document.getElementById('new-admin-password')?.value || '';
    const confirmedPassword = document.getElementById('confirm-admin-password')?.value || '';
    const error = document.getElementById('password-change-error');
    const button = document.getElementById('btn-save-password');

    if (error) error.innerText = '';
    if (newPassword !== confirmedPassword) {
        if (error) error.innerText = '两次输入的新密码不一致';
        return;
    }
    if (newPassword.length < 12) {
        if (error) error.innerText = '新密码至少需要 12 个字符';
        return;
    }

    if (button) {
        button.disabled = true;
        button.innerText = '保存中...';
    }

    try {
        const res = await apiFetch('/api/auth/change-password', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ currentPassword, newPassword })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            if (error && appState.authenticated) error.innerText = data.error || '密码修改失败';
            return;
        }

        document.getElementById('form-change-password')?.reset();
        closeModal('modal-password');
        showToast('管理员密码已修改，其它旧会话已失效', 'success');
    } catch (err) {
        if (error) error.innerText = `请求失败：${err.message}`;
    } finally {
        if (button) {
            button.disabled = false;
            button.innerText = '保存新密码';
        }
    }
}

function openPlaybackSecurityModal() {
    renderPlaybackSecurityState();
    const error = document.getElementById('playback-token-error');
    const password = document.getElementById('playback-token-current-password');
    if (error) error.innerText = '';
    if (password) password.value = '';
    openModal('modal-playback-security');
}

function renderPlaybackSecurityState() {
    const url = appState.status?.m3uUrl || '';
    const authEnabled = appState.status?.playbackTokenAuthEnabled !== false;
    const configured = appState.status?.playbackTokenConfigured === true;
    const available = appState.status?.playbackTokenAvailable === true && Boolean(url);
    const accessAvailable = Boolean(url) && (!authEnabled || available);
    const input = document.getElementById('playback-m3u-url');
    const copyButton = document.getElementById('btn-copy-secure-m3u');
    const status = document.getElementById('playback-token-status');
    const toggle = document.getElementById('playback-token-auth-enabled');

    if (toggle && !toggle.disabled) toggle.checked = authEnabled;
    if (input) input.value = accessAvailable ? url : '';
    if (copyButton) copyButton.disabled = !accessAvailable;
    if (status) {
        status.className = `playback-token-status${authEnabled && available ? '' : ' missing'}`;
        if (!authEnabled) {
            status.innerText = '⚠️ 播放令牌鉴权已关闭；任何能够访问本服务的设备都可以读取并播放频道。';
        } else if (available) {
            status.innerText = '✅ 播放令牌鉴权已启用；新轮换令牌为纯 6 位数字，网页登录退出或过期不会影响播放。';
        } else if (configured) {
            status.innerText = '⚠️ 播放令牌已配置，但当前会话无法解密恢复地址；请轮换令牌。';
        } else {
            status.innerText = '⚠️ 尚未配置播放令牌，请先轮换生成 6 位数字令牌。';
        }
    }
}

async function updatePlaybackTokenAuthentication(event) {
    const toggle = event.currentTarget;
    const enabled = toggle.checked;
    const error = document.getElementById('playback-token-error');
    if (error) error.innerText = '';

    if (!enabled) {
        const confirmed = await showConfirm({
            title: '关闭播放令牌鉴权',
            message: '关闭后，任何能够访问本服务地址的设备都可以获取 M3U、HLS 和分片。确认仅在可信局域网中使用并继续吗？',
            okText: '确认关闭',
            cancelText: '保持启用',
            type: 'danger'
        });
        if (!confirmed) {
            toggle.checked = true;
            return;
        }
    }

    toggle.disabled = true;
    try {
        const res = await apiFetch('/api/playback-token/auth', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            toggle.checked = !enabled;
            if (error) error.innerText = data.error || '播放令牌鉴权设置失败';
            return;
        }

        await loadStatus(false);
        showToast(enabled ? '播放令牌鉴权已启用' : '播放令牌鉴权已关闭', enabled ? 'success' : 'warning');
    } catch (err) {
        toggle.checked = !enabled;
        if (error) error.innerText = `请求失败：${err.message}`;
    } finally {
        toggle.disabled = false;
        renderPlaybackSecurityState();
    }
}

async function rotatePlaybackToken(event) {
    event.preventDefault();
    const currentPassword = document.getElementById('playback-token-current-password')?.value || '';
    const error = document.getElementById('playback-token-error');
    const button = document.getElementById('btn-rotate-playback-token');
    if (error) error.innerText = '';

    const authEnabled = appState.status?.playbackTokenAuthEnabled !== false;
    const confirmed = await showConfirm({
        title: '轮换 6 位数字令牌',
        message: authEnabled
            ? '旧的带令牌 M3U 与全部 HLS 地址会立即失效。确认已准备好把新地址更新到 Jellyfin / IPTV 客户端吗？'
            : '将生成新的 6 位数字令牌；当前无令牌地址保持不变，重新启用鉴权后需要使用新地址。确认继续吗？',
        okText: '确认轮换',
        cancelText: '暂不轮换',
        type: 'danger'
    });
    if (!confirmed) return;

    if (button) {
        button.disabled = true;
        button.innerText = '正在生成...';
    }

    try {
        const res = await apiFetch('/api/playback-token/rotate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ currentPassword })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            if (error) error.innerText = data.error || '播放令牌轮换失败';
            return;
        }

        if (document.getElementById('playback-token-current-password'))
            document.getElementById('playback-token-current-password').value = '';
        await loadStatus(false);
        renderPlaybackSecurityState();
        showToast(authEnabled ? '6 位数字令牌已轮换，请更新客户端地址' : '6 位数字令牌已轮换', 'success');
    } catch (err) {
        if (error) error.innerText = `请求失败：${err.message}`;
    } finally {
        if (button) {
            button.disabled = false;
            button.innerText = '轮换 6 位数字令牌';
        }
    }
}

// -------------------------------------------------------------
// API Data Fetching
// -------------------------------------------------------------

async function loadConfig() {
    try {
        const res = await apiFetch('/api/config');
        if (res.ok) {
            const data = await res.json();
            appState.config = data;
            if (data.cookieStatuses) {
                appState.cookieStatuses = data.cookieStatuses;
            }
            updateCookieStatusSummary();
        }
    } catch (err) {
        console.error('加载配置失败:', err);
    }
}

async function loadStatus(showToastOnSuccess = false) {
    try {
        const res = await apiFetch('/api/status');
        if (res.ok) {
            const data = await res.json();
            appState.status = data;
            if (data.cookieStatuses) {
                appState.cookieStatuses = data.cookieStatuses;
            }
            renderDashboard();
            updateCookieStatusSummary();
            if (document.getElementById('modal-playback-security')?.classList.contains('active'))
                renderPlaybackSecurityState();
            if (showToastOnSuccess) {
                showToast('状态已刷新', 'success');
            }
        }
    } catch (err) {
        console.error('获取系统状态失败:', err);
    }
}

function updateCookieStatusSummary() {
    const configured = appState.config?.cookieConfigured || {};
    const statuses = appState.cookieStatuses || {};
    const platforms = ['huya', 'douyu', 'bilibili'];

    let hasExpired = false;
    let configuredCount = 0;

    platforms.forEach(p => {
        const hasVal = configured[p] === true;
        if (hasVal) {
            configuredCount++;
            if (statuses[p] && statuses[p].isValid === false) {
                hasExpired = true;
            }
        }
    });

    const summaryEl = document.getElementById('stat-cookie-count');
    if (summaryEl) {
        if (hasExpired) {
            summaryEl.innerHTML = '<span style="color: var(--danger); font-weight: 600;">⚠️ 存在失效 Cookie</span>';
        } else if (configuredCount === 3) {
            summaryEl.innerHTML = '<span style="color: var(--success);">全部已授权 (有效)</span>';
        } else if (configuredCount > 0) {
            summaryEl.innerHTML = `<span style="color: var(--success);">${configuredCount} 个平台已授权</span>`;
        } else {
            summaryEl.innerHTML = '<span style="color: var(--text-muted);">全平台免登录</span>';
        }
    }
}

// -------------------------------------------------------------
// Dashboard Rendering
// -------------------------------------------------------------

function renderDashboard() {
    if (!appState.status) return;

    const s = appState.status;

    // Version display
    if (s.version) {
        const verEl = document.getElementById('app-version');
        if (verEl) verEl.innerText = s.version;
    }

    // Overview stats
    document.getElementById('stat-active-streams').innerText = `${s.activeStreams} / ${s.totalChannels}`;
    document.getElementById('stat-uptime').innerText = s.uptimeText || '刚刚启动';
    
    // 局域网中继端点：优先显示配置的 customHost，其次显示后端识别的 displayHost，最后显示 window.location.host
    let activeHost = appState.config?.customHost || s.displayHost || window.location.host;
    if (activeHost && !activeHost.includes(':') && s.httpPort) {
        activeHost = `${activeHost}:${s.httpPort}`;
    }
    document.getElementById('stat-local-ip').innerText = activeHost || `${s.localIp}:${s.httpPort}`;
    document.getElementById('badge-total-channels').innerText = `${s.totalChannels} 个频道`;

    // Render channels
    const container = document.getElementById('channels-container');
    if (!s.channels || s.channels.length === 0) {
        container.innerHTML = `
            <div class="loading-placeholder">
                <p>暂无配置任何直播频道</p>
                <button class="btn btn-primary" style="margin-top: 12px;" onclick="openAddChannelModal()">+ 立即添加第一个频道</button>
            </div>
        `;
        return;
    }

    container.innerHTML = s.channels.map(ch => createChannelCardHtml(ch)).join('');
}

function createChannelCardHtml(ch) {
    const platformClass = `platform-${ch.platform?.toLowerCase() || 'huya'}`;
    const platformName = getPlatformDisplayName(ch.platform);

    let statusBadgeClass = 'waiting';
    let statusPillHtml = '';

    const statusTitle = ch.retryCount > 0 
        ? `${ch.statusMessage || ''} (重试 ${ch.retryCount} 次)`
        : (ch.statusMessage || '');

    // 状态分类判断与右上角状态胶囊
    if (!ch.enable) {
        statusBadgeClass = 'disabled';
        statusPillHtml = `<span class="status-dot-pulse disabled"></span><span>已停用</span>`;
    } else if (ch.isLive || ch.channelState === 'Streaming') {
        statusBadgeClass = 'live';
        statusPillHtml = `
            <span class="live-wave-anim" title="正在实时推流">
                <span class="bar"></span>
                <span class="bar"></span>
                <span class="bar"></span>
            </span>
            <span>推流中</span>
        `;
    } else if (ch.channelState === 'Ready' || ch.statusMessage === '已开播' || ch.statusMessage?.includes('已开播')) {
        statusBadgeClass = 'ready';
        statusPillHtml = `<span class="status-dot-pulse ready"></span><span>已开播</span>`;
    } else if (ch.channelState === 'Offline' || ch.statusMessage?.includes('未开播')) {
        statusBadgeClass = 'offline';
        statusPillHtml = `<span class="status-dot-pulse offline"></span><span>未开播</span>`;
    } else if (ch.channelState === 'Starting' || ch.channelState === 'Restarting' || ch.statusMessage?.includes('启动') || ch.statusMessage?.includes('重启')) {
        statusBadgeClass = 'starting';
        statusPillHtml = `<span class="status-dot-pulse starting"></span><span>启动中...</span>`;
    } else if (ch.statusMessage?.includes('Cookie') || ch.statusMessage?.includes('登录')) {
        statusBadgeClass = 'error';
        statusPillHtml = `<span class="status-dot-pulse error"></span><span>Cookie失效</span>`;
    } else if (ch.statusMessage?.includes('检测中') || ch.statusMessage?.includes('初始化')) {
        statusBadgeClass = 'waiting';
        statusPillHtml = `<span class="status-dot-pulse waiting"></span><span>检测中...</span>`;
    } else {
        statusBadgeClass = 'error';
        const errShort = (ch.statusMessage && ch.statusMessage.length > 8) ? '异常' : (ch.statusMessage || '异常');
        statusPillHtml = `<span class="status-dot-pulse error"></span><span>${escapeHtml(errShort)}</span>`;
    }

    // 平台 Cookie 状态与真伪检测状态展示
    const platformKey = (ch.platform || 'huya').toLowerCase();
    const hasPlatformCookie = appState.config?.cookieConfigured?.[platformKey] === true || ch.isCookieConfigured === true;
    const cookieStatus = appState.cookieStatuses?.[platformKey] || {
        configured: ch.isCookieConfigured ?? hasPlatformCookie,
        isValid: ch.isCookieValid ?? true,
        isNetworkError: ch.isCookieNetworkError ?? false,
        username: ch.cookieUsername ?? '',
        message: ch.cookieStatusMessage ?? ''
    };

    let cookieDisplayText = `<span class="cookie-tag-inline muted">⚪ 免登录</span>`;
    if (hasPlatformCookie) {
        if (cookieStatus.isValid === false && !cookieStatus.isNetworkError) {
            // 明确确认过期，高亮红字
            cookieDisplayText = `<span class="cookie-tag-inline expired" title="${escapeHtml(cookieStatus.message || 'Cookie已失效')}">🔴 已过期</span>`;
        } else if (cookieStatus.isNetworkError) {
            // 网络检测异常，保持有效授权状态
            cookieDisplayText = `<span class="cookie-tag-inline active" title="${escapeHtml(cookieStatus.message || '网络检测波动，维持授权状态')}">🟢 已授权</span>`;
        } else {
            cookieDisplayText = `<span class="cookie-tag-inline active" title="${escapeHtml(cookieStatus.message || 'Cookie有效并已授权')}">🟢 已授权</span>`;
        }
    }

    // 试播按钮可用状态 (只有推流中才能试播)
    // 试播纯图标按钮 (与右侧重启/编辑/删除保持一致)
    const canPreview = ch.isLive && Boolean(ch.hlsUrl);
    const previewBtnHtml = canPreview
        ? `<button class="btn-icon" onclick="openPreviewModal('${ch.id}', '${escapeHtml(ch.name)}', '${ch.hlsUrl}')" title="试播">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor">
                    <polygon points="6 3 20 12 6 21 6 3"></polygon>
                </svg>
           </button>`
        : `<button class="btn-icon btn-disabled" disabled title="试播 (当前未推流)">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor">
                    <polygon points="6 3 20 12 6 21 6 3"></polygon>
                </svg>
           </button>`;

    return `
        <div class="channel-card ${ch.enable ? '' : 'disabled'}" id="card-${ch.id}">
            <div>
                <div class="channel-header">
                    <div class="channel-title-wrap">
                        <span class="platform-pill ${platformClass}">${platformName}</span>
                        <h3 class="channel-name" title="${escapeHtml(ch.name)}">${escapeHtml(ch.name)}</h3>
                    </div>
                    <span class="status-pill ${statusBadgeClass}" title="${escapeHtml(statusTitle)}">${statusPillHtml}</span>
                </div>

                <div class="channel-info-list">
                    <div class="info-item">
                        <span class="info-label">频道 ID:</span>
                        <span class="info-value">${escapeHtml(ch.id)}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">画质等级:</span>
                        <span class="info-value">${escapeHtml(ch.quality || 'OD')}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">Cookie 授权:</span>
                        <span class="info-value">${cookieDisplayText}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">源链接:</span>
                        <span class="info-value" title="${escapeHtml(ch.url)}">${escapeHtml(ch.url)}</span>
                    </div>
                </div>
            </div>

            <div class="channel-actions">
                <label class="switch-label" title="切换频道开启/关闭">
                    <input type="checkbox" ${ch.enable ? 'checked' : ''} onchange="toggleChannel('${ch.id}')">
                    <span class="switch-slider"></span>
                </label>

                <div class="action-btns">
                    ${previewBtnHtml}
                    <button class="btn-icon" onclick="restartChannel('${ch.id}')" title="重启推流进程">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="23 4 23 10 17 10"></polyline>
                            <polyline points="1 20 1 14 7 14"></polyline>
                            <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"></path>
                        </svg>
                    </button>
                    <button class="btn-icon" onclick="openEditChannelModal('${ch.id}')" title="编辑频道">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"></path>
                            <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"></path>
                        </svg>
                    </button>
                    <button class="btn-icon btn-danger-outline" onclick="deleteChannel('${ch.id}', '${escapeHtml(ch.name)}')" title="删除频道">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="3 6 5 6 21 6"></polyline>
                            <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                        </svg>
                    </button>
                </div>
            </div>
        </div>
    `;
}

function getPlatformDisplayName(platform) {
    switch (platform?.toLowerCase()) {
        case 'huya': return '虎牙';
        case 'douyu': return '斗鱼';
        case 'bilibili': return 'B站';
        default: return platform || '未知';
    }
}

// -------------------------------------------------------------
// URL Parsing & Smart Platform Detection
// -------------------------------------------------------------

function detectPlatformFromUrl(rawUrl) {
    if (!rawUrl) return 'huya';
    const str = rawUrl.toLowerCase();
    if (str.includes('bilibili.com') || str.includes('b23.tv')) return 'bilibili';
    if (str.includes('douyu.com')) return 'douyu';
    if (str.includes('huya.com')) return 'huya';
    return 'huya';
}

function sanitizeUrl(rawUrl, platform) {
    if (!rawUrl) return '';
    let str = rawUrl.trim();

    if (platform === 'bilibili' || str.includes('bilibili.com') || str.includes('b23.tv')) {
        const m = str.match(/(?:live\.bilibili\.com\/|b23\.tv\/)(\d+)/i);
        if (m) return `https://live.bilibili.com/${m[1]}`;
    } else if (platform === 'douyu' || str.includes('douyu.com')) {
        const mRid = str.match(/rid=(\d+)/i);
        const mNum = str.match(/douyu\.com\/(\d+)/i);
        if (mRid) return `https://www.douyu.com/${mRid[1]}`;
        if (mNum) return `https://www.douyu.com/${mNum[1]}`;
    } else if (platform === 'huya' || str.includes('huya.com')) {
        const m = str.match(/huya\.com\/([a-zA-Z0-9_-]+)/i);
        if (m) return `https://www.huya.com/${m[1]}`;
    }

    // 默认剥离 query 参数
    return str.split('?')[0];
}

function updatePlatformUi(platform, isUserUrlPresent = false) {
    if (platform) {
        document.getElementById('channel-platform').value = platform;
    }
    const badge = document.getElementById('detected-platform-badge');
    const cookieText = document.getElementById('channel-cookie-status-text');
    const cookieBox = document.getElementById('channel-cookie-status-display');

    if (!isUserUrlPresent || !platform) {
        // 未填入链接时的初始空状态
        if (badge) badge.style.display = 'none';
        if (cookieText && cookieBox) {
            cookieBox.className = 'static-field-display';
            cookieText.innerHTML = '等待输入链接...';
        }
        return;
    }

    const hasCookie = appState.config?.cookieConfigured?.[platform] === true;
    const pName = getPlatformDisplayName(platform);

    if (badge) {
        badge.style.display = 'inline-flex';
        badge.className = `detected-platform-tag tag-${platform}`;
        badge.innerHTML = `🏷️ 已识别平台：<strong>${pName}直播</strong> (链接已自动规范化)`;
    }

    if (cookieText && cookieBox) {
        if (hasCookie) {
            cookieBox.className = 'static-field-display active';
            cookieText.innerHTML = `<strong>已授权</strong> (已自动绑定)`;
        } else {
            cookieBox.className = 'static-field-display';
            cookieText.innerHTML = `免登录模式 (可在平台Cookie中配置)`;
        }
    }
}

function onUrlInput() {
    clearTimeout(appState.urlDebounceTimer);
    const urlInput = document.getElementById('channel-url');
    const rawVal = urlInput?.value?.trim() || '';

    if (!rawVal) {
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawVal);
    updatePlatformUi(platform, true);

    // 400ms 防抖自动清理 URL 并自动识别名称
    appState.urlDebounceTimer = setTimeout(() => {
        const clean = sanitizeUrl(rawVal, platform);
        if (clean && clean !== rawVal) {
            urlInput.value = clean;
        }
        autoFetchChannelInfo(true);
    }, 400);
}

function onUrlBlur() {
    const urlInput = document.getElementById('channel-url');
    const rawVal = urlInput?.value?.trim() || '';
    if (!rawVal) {
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawVal);
    const clean = sanitizeUrl(rawVal, platform);
    if (clean && clean !== rawVal) {
        urlInput.value = clean;
    }
    updatePlatformUi(platform, true);

    const nameInput = document.getElementById('channel-name');
    if (nameInput && !nameInput.value.trim()) {
        autoFetchChannelInfo(true);
    }
}

async function autoFetchChannelInfo(silent = false) {
    const urlInput = document.getElementById('channel-url');
    const rawUrl = urlInput?.value?.trim();
    const nameInput = document.getElementById('channel-name');
    const idInput = document.getElementById('channel-id');
    const btn = document.getElementById('btn-auto-fetch');

    if (!rawUrl) {
        if (!silent) showToast('请先输入直播间链接或房间号', 'error');
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawUrl);
    updatePlatformUi(platform, true);

    if (btn) {
        btn.innerText = '⏳ 识别中...';
        btn.disabled = true;
    }

    try {
        const res = await apiFetch(`/api/channels/fetch-info?platform=${encodeURIComponent(platform)}&url=${encodeURIComponent(rawUrl)}`);
        if (res.ok) {
            const data = await res.json();
            if (data.cleanUrl && urlInput) {
                urlInput.value = data.cleanUrl;
            }
            if (data.platform) {
                updatePlatformUi(data.platform, true);
            }
            if (data.name && nameInput) {
                nameInput.value = data.name;
                nameInput.classList.add('highlight-flash');
                setTimeout(() => nameInput.classList.remove('highlight-flash'), 1000);
            }
            if (data.suggestedId && idInput && !idInput.disabled && (!idInput.value || !appState.editingChannelId)) {
                idInput.value = data.suggestedId;
                idInput.classList.add('highlight-flash');
                setTimeout(() => idInput.classList.remove('highlight-flash'), 1000);
            }
            if (!silent && data.success) {
                showToast(`已识别：${data.name}`, 'success');
            }
        }
    } catch (err) {
        if (!silent) showToast('识别失败: ' + err.message, 'error');
    } finally {
        if (btn) {
            btn.innerText = '✨ 自动识别';
            btn.disabled = false;
        }
    }
}

// -------------------------------------------------------------
// Channel Add / Edit / Delete / Toggle
// -------------------------------------------------------------

function openAddChannelModal() {
    appState.editingChannelId = null;
    document.getElementById('modal-channel-title').innerText = '添加直播频道';
    document.getElementById('channel-id').value = '';
    document.getElementById('channel-id').disabled = false;
    document.getElementById('channel-name').value = '';
    document.getElementById('channel-url').value = '';
    document.getElementById('channel-quality').value = 'OD';
    document.getElementById('channel-enable').checked = true;

    // 默认空状态，不显示任何平台识别或Cookie匹配标签
    updatePlatformUi(null, false);
    openModal('modal-channel');
}

function openEditChannelModal(id) {
    const channel = appState.config?.channels?.find(c => c.id === id) || 
                    appState.status?.channels?.find(c => c.id === id);
    if (!channel) return;

    appState.editingChannelId = id;
    document.getElementById('modal-channel-title').innerText = '编辑直播频道';
    document.getElementById('channel-id').value = channel.id;
    document.getElementById('channel-id').disabled = true; // ID 不可修改
    document.getElementById('channel-name').value = channel.name;
    document.getElementById('channel-url').value = channel.url;
    document.getElementById('channel-quality').value = channel.quality || 'OD';
    document.getElementById('channel-enable').checked = channel.enable !== false;

    const platform = channel.platform?.toLowerCase() || detectPlatformFromUrl(channel.url);
    updatePlatformUi(platform, true);

    openModal('modal-channel');
}

async function saveChannel(event) {
    event.preventDefault();

    const id = document.getElementById('channel-id').value.trim();
    const name = document.getElementById('channel-name').value.trim();
    const platform = document.getElementById('channel-platform').value || 'huya';
    let url = document.getElementById('channel-url').value.trim();
    const quality = document.getElementById('channel-quality').value;
    const enable = document.getElementById('channel-enable').checked;

    url = sanitizeUrl(url, platform);

    const payload = {
        id: appState.editingChannelId || id,
        name,
        platform,
        url,
        quality,
        cookieProfileKey: platform,
        enable
    };

    try {
        const res = await apiFetch('/api/channels', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (res.ok) {
            showToast(appState.editingChannelId ? '频道已更新' : '频道已添加并开始推流', 'success');
            closeModal('modal-channel');
            await loadConfig();
            await loadStatus();
        } else {
            const err = await res.json();
            showToast(err.error || '保存失败', 'error');
        }
    } catch (err) {
        showToast('网络请求失败: ' + err.message, 'error');
    }
}

async function deleteChannel(id, name) {
    const confirmed = await showConfirm({
        title: '删除直播频道',
        message: `确定要删除频道 "${name}" 吗？此操作不可恢复。`,
        okText: '确定删除',
        cancelText: '取消',
        type: 'danger'
    });
    if (!confirmed) return;

    try {
        const res = await apiFetch(`/api/channels/${encodeURIComponent(id)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            showToast(`频道 "${name}" 已删除`, 'success');
            await loadConfig();
            await loadStatus();
        } else {
            showToast('删除失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function toggleChannel(id) {
    try {
        const res = await apiFetch(`/api/channels/${encodeURIComponent(id)}/toggle`, {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            showToast(data.enable ? '已启用频道推流' : '已停用频道推流', 'success');
            await loadConfig();
            await loadStatus();
        }
    } catch (err) {
        showToast('切换状态失败', 'error');
    }
}

async function restartChannel(id) {
    try {
        const res = await apiFetch(`/api/channels/${encodeURIComponent(id)}/restart`, {
            method: 'POST'
        });

        if (res.ok) {
            showToast('已触发推流进程重启', 'success');
            await loadStatus();
        }
    } catch (err) {
        showToast('重启失败', 'error');
    }
}

// -------------------------------------------------------------
// Platform Cookie Management (固定三大平台)
// -------------------------------------------------------------

function openCookieModal() {
    renderPlatformCookieCards();
    openModal('modal-cookies');
}

function togglePlatformCookieEdit(platform, forceState = null) {
    const editor = document.getElementById(`editor-cookie-${platform}`);
    const btnEdit = document.getElementById(`btn-edit-${platform}`);
    if (!editor) return;

    const isCurrentlyOpen = editor.style.display !== 'none';
    const nextState = forceState !== null ? forceState : !isCurrentlyOpen;

    editor.style.display = nextState ? 'flex' : 'none';
    appState.cookieEditors[platform] = nextState;

    if (btnEdit) {
        btnEdit.innerText = nextState ? '🔼 收起' : '✏️ 编辑';
    }

    if (nextState) {
        const textarea = document.getElementById(`cookie-${platform}`);
        textarea?.focus();
    }
}

function renderPlatformCookieCards() {
    const configured = appState.config?.cookieConfigured || {};
    const statuses = appState.cookieStatuses || {};
    const platforms = ['huya', 'douyu', 'bilibili'];

    platforms.forEach(p => {
        const textarea = document.getElementById(`cookie-${p}`);
        const tag = document.getElementById(`${p}-cookie-tag`);
        const card = document.getElementById(`card-cookie-${p}`);
        const btnClear = document.getElementById(`btn-clear-${p}`);
        const btnVerify = document.getElementById(`btn-verify-${p}`);
        const status = statuses[p];

        const isConfigured = configured[p] === true;
        if (textarea && document.activeElement !== textarea) {
            textarea.value = '';
            textarea.placeholder = isConfigured
                ? '已配置且不会回传原文；如需替换，请粘贴新的 Cookie...'
                : '在此粘贴新的 Cookie...';
        }

        if (card) {
            if (isConfigured) card.classList.add('configured');
            else card.classList.remove('configured');
        }

        if (btnClear) {
            btnClear.style.display = isConfigured ? 'inline-block' : 'none';
        }
        if (btnVerify) {
            btnVerify.style.display = isConfigured ? 'inline-block' : 'none';
        }

        if (tag) {
            if (!isConfigured) {
                tag.className = 'cookie-badge-status';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">未配置 (免登录模式)</span>`;
            } else if (status && status.isValid === true) {
                tag.className = 'cookie-badge-status valid';
                const userText = status.username ? `已授权: ${status.username}` : (status.message || '已授权有效');
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">${escapeHtml(userText)}</span>`;
            } else if (status && status.isNetworkError) {
                tag.className = 'cookie-badge-status valid';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已配置 (网络波动，保持状态)</span>`;
            } else if (status && status.isValid === false) {
                tag.className = 'cookie-badge-status expired';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已过期: ${escapeHtml(status.message || '账号未登录')}</span>`;
            } else {
                tag.className = 'cookie-badge-status';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已配置</span>`;
            }
        }
    });
}

async function verifyPlatformCookie(platform) {
    const btn = document.getElementById(`btn-verify-${platform}`);
    const tag = document.getElementById(`${platform}-cookie-tag`);
    const pName = getPlatformDisplayName(platform);

    if (btn) {
        btn.disabled = true;
        btn.innerText = '⏳ 检测中...';
    }
    if (tag) {
        tag.className = 'cookie-badge-status checking';
        tag.innerHTML = `<span class="status-dot"></span><span class="status-text">正在鉴权检测...</span>`;
    }

    try {
        const res = await apiFetch(`/api/cookies/verify?platform=${encodeURIComponent(platform)}`, {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            renderPlatformCookieCards();
            renderDashboard();
            updateCookieStatusSummary();

            const st = data.status || appState.cookieStatuses[platform];
            if (st?.isValid && !st?.isNetworkError) {
                showToast(`${pName} Cookie 检测通过 (${st.username ? '用户: ' + st.username : st.message})`, 'success');
            } else if (st?.isNetworkError) {
                showToast(`${pName} Cookie 检测提示: ${st.message}，保持当前授权状态`, 'warning');
            } else {
                showToast(`${pName} Cookie 检测失败: ${st?.message || '已失效'}`, 'error');
            }
        } else {
            showToast(`${pName} 检测请求失败`, 'error');
            renderPlatformCookieCards();
        }
    } catch (err) {
        showToast('检测请求异常: ' + err.message, 'error');
        renderPlatformCookieCards();
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerText = '🔍 检测';
        }
    }
}

async function verifyAllPlatformCookies() {
    showToast('正在检测全部平台 Cookie 状态...', 'info');
    try {
        const res = await apiFetch('/api/cookies/verify?platform=all', {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            renderPlatformCookieCards();
            renderDashboard();
            updateCookieStatusSummary();
            showToast('已完成全部平台有效性检测', 'success');
        } else {
            showToast('检测失败', 'error');
        }
    } catch (err) {
        showToast('检测请求失败: ' + err.message, 'error');
    }
}

async function savePlatformCookie(platform) {
    const textarea = document.getElementById(`cookie-${platform}`);
    const cookie = textarea?.value?.trim() || '';
    const pName = getPlatformDisplayName(platform);

    if (!cookie) {
        showToast(`请粘贴新的 ${pName} Cookie；如需删除请使用“清空”按钮`, 'warning');
        textarea?.focus();
        return;
    }

    try {
        const res = await apiFetch('/api/cookies', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ key: platform, cookie })
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            togglePlatformCookieEdit(platform, false);
            await loadConfig();
            await loadStatus();
            renderPlatformCookieCards();

            const st = data.status || appState.cookieStatuses[platform];
            if (st?.isValid) {
                showToast(`${pName} Cookie 已保存并验证通过 (${st.username || st.message})`, 'success');
            } else if (cookie.length > 0) {
                showToast(`${pName} Cookie 已保存，但检测提示: ${st?.message || '可能已失效'}`, 'warning');
            } else {
                showToast(`${pName} Cookie 已清空 (免登录)`, 'success');
            }
        } else {
            showToast('保存 Cookie 失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function clearPlatformCookie(platform) {
    const pName = getPlatformDisplayName(platform);
    const confirmed = await showConfirm({
        title: `清空 ${pName} Cookie`,
        message: `确定要清空 ${pName} 的 Cookie 凭据吗？关联此平台的频道将自动降级为免登录模式。`,
        okText: '确定清空',
        cancelText: '取消',
        type: 'danger'
    });
    if (!confirmed) return;

    try {
        const res = await apiFetch(`/api/cookies/${encodeURIComponent(platform)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            togglePlatformCookieEdit(platform, false);
            showToast(`已清空 ${pName} Cookie`, 'success');
            await loadConfig();
            await loadStatus();
            renderPlatformCookieCards();
        } else {
            showToast('清空失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

// -------------------------------------------------------------
// Live Preview Player (Hls.js)
// -------------------------------------------------------------

function openPreviewModal(id, name, relativeHlsUrl) {
    const video = document.getElementById('preview-video');
    const title = document.getElementById('preview-title');
    const urlDisplay = document.getElementById('preview-hls-url');
    const errorMsg = document.getElementById('player-error-msg');

    title.innerText = `试播：${name}`;
    errorMsg.style.display = 'none';

    const fullUrl = window.location.origin + relativeHlsUrl;
    urlDisplay.innerText = fullUrl;

    openModal('modal-preview');
    resetPreviewPlayback();

    const token = ++appState.previewToken;
    appState.previewUrl = fullUrl;
    appState.previewRecoveryAttempts = 0;
    appState.previewMediaRecoveries = 0;
    appState.previewHardRecoveries = 0;
    appState.previewLastHardRecoveryAt = 0;
    appState.previewLastMediaRecoveryAt = 0;
    appState.previewLastSoftRecoveryAt = 0;
    appState.previewSevereErrorCount = 0;
    appState.previewLastSevereErrorAt = 0;
    appState.previewLastProgressAt = performance.now();
    appState.previewLastTime = 0;
    appState.previewBufferedFragments = 0;
    appState.previewLastFragEndPts = null;
    appState.previewLastFragSequence = null;
    appState.previewLastFragContinuity = null;
    appState.previewAudioCodecSignature = null;
    appState.previewRecoveryExhausted = false;

    if (typeof Hls !== 'undefined' && Hls.isSupported()) {
        createHlsPreviewPlayer(fullUrl, token);
        startPreviewStallMonitor(token);
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        // Native Safari / iOS HLS support
        bindPreviewMediaProgressEvents(video, token);
        video.src = fullUrl;
        video.onloadedmetadata = () => {
            video.play().catch(() => {});
        };
        video.onstalled = () => recoverNativePreview(video);
        video.onerror = () => {
            if (token !== appState.previewToken) return;
            errorMsg.innerText = '直播连接中断，正在重新加载...';
            errorMsg.style.display = 'block';
            window.clearTimeout(appState.hlsRetryTimer);
            appState.hlsRetryTimer = window.setTimeout(() => {
                appState.hlsRetryTimer = null;
                if (token !== appState.previewToken) return;
                video.src = fullUrl;
                video.load();
                video.play().catch(() => {});
            }, 1500);
        };
    } else {
        errorMsg.innerText = '当前浏览器不支持 HLS 视频直接播放，请复制链接到外部播放器观看。';
        errorMsg.style.display = 'block';
    }
}

function createHlsPreviewPlayer(fullUrl, token) {
    const video = document.getElementById('preview-video');
    const errorMsg = document.getElementById('player-error-msg');
    if (!video || token !== appState.previewToken) return;

    // 每次重连都释放旧 MediaSource/SourceBuffer，避免沿用异常的音频解码状态。
    destroyHlsPreviewInstance(video);
    resetPreviewGenerationState();

    const hls = new Hls({
        enableWorker: true,
        lowLatencyMode: false,
        maxBufferLength: 30,
        maxMaxBufferLength: 60,
        backBufferLength: 30,
        liveSyncDurationCount: 3,
        liveMaxLatencyDurationCount: 8,
        manifestLoadingTimeOut: 10000,
        manifestLoadingMaxRetry: 4,
        manifestLoadingRetryDelay: 1000,
        levelLoadingTimeOut: 10000,
        levelLoadingMaxRetry: 4,
        fragLoadingTimeOut: 20000,
        fragLoadingMaxRetry: 6,
        detectStallWithCurrentTimeMs: 2500,
        nudgeOnVideoHole: true,
        liveSyncOnStallIncrease: 1
    });
    appState.hlsPlayer = hls;
    bindPreviewMediaProgressEvents(video, token);

    hls.on(Hls.Events.MEDIA_ATTACHED, () => {
        if (token === appState.previewToken && hls === appState.hlsPlayer)
            hls.loadSource(fullUrl);
    });

    hls.on(Hls.Events.MANIFEST_PARSED, () => {
        if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;
        video.play().catch(() => {});
    });

    hls.on(Hls.Events.BUFFER_CODECS, (_event, data) => {
        if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;
        detectPreviewAudioCodecChange(data, fullUrl, token);
    });

    hls.on(Hls.Events.LEVEL_PTS_UPDATED, (_event, data) => {
        if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;
        detectPreviewTimestampDrift(data, fullUrl, token);
    });

    hls.on(Hls.Events.FRAG_BUFFERED, (_event, data) => {
        if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;
        inspectPreviewFragmentTimeline(data, fullUrl, token);
        // 分片到达不等于播放器实际前进，不能在这里刷新播放进展或恢复次数。
        if (!appState.hlsRetryTimer && !appState.previewRecoveryExhausted)
            errorMsg.style.display = 'none';
    });

    hls.on(Hls.Events.ERROR, (_event, data) => {
        handleHlsPreviewError(hls, video, fullUrl, token, data);
    });

    video.onstalled = () => {
        if (hls !== appState.hlsPlayer) return;
        handlePreviewPlaybackStall(hls, video, fullUrl, token);
    };
    video.onerror = () => {
        if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;
        const code = video.error?.code ?? 'unknown';
        // 先让 hls.js 的 ERROR 处理器执行；只有它没有接管时才升级为深度重建。
        window.setTimeout(() => {
            if (token !== appState.previewToken || hls !== appState.hlsPlayer || appState.hlsRetryTimer) return;
            const recentlyRecovered = appState.previewLastMediaRecoveryAt > 0 &&
                performance.now() - appState.previewLastMediaRecoveryAt < 1000;
            if (!recentlyRecovered)
                scheduleHardPreviewRecovery(fullUrl, token, `浏览器媒体解码异常 (${code})`);
        }, 250);
    };
    hls.attachMedia(video);
}

function scheduleHlsPreviewReload(fullUrl, token, reason) {
    if (token !== appState.previewToken || appState.hlsRetryTimer) return;

    const errorMsg = document.getElementById('player-error-msg');
    if (appState.previewRecoveryAttempts >= PREVIEW_MAX_RELOAD_ATTEMPTS) {
        appState.previewRecoveryExhausted = true;
        errorMsg.innerText = `${reason}。自动重连已达上限，请关闭后重新打开试播。`;
        errorMsg.style.display = 'block';
        return;
    }

    appState.previewRecoveryAttempts++;
    const delay = Math.min(1000 * Math.pow(2, Math.min(appState.previewRecoveryAttempts - 1, 3)), 8000);
    errorMsg.innerText = `${reason}，${Math.ceil(delay / 1000)} 秒后重新连接...`;
    errorMsg.style.display = 'block';

    appState.hlsRetryTimer = window.setTimeout(() => {
        appState.hlsRetryTimer = null;
        if (token !== appState.previewToken) return;
        createHlsPreviewPlayer(fullUrl, token);
    }, delay);
}

function scheduleHardPreviewRecovery(fullUrl, token, reason) {
    if (token !== appState.previewToken || appState.hlsRetryTimer) return;

    const errorMsg = document.getElementById('player-error-msg');
    if (appState.previewHardRecoveries >= PREVIEW_MAX_HARD_RECOVERIES) {
        appState.previewRecoveryExhausted = true;
        errorMsg.innerText = `${reason}。深度恢复已达上限；若声音仍异常，请重启该频道推流。`;
        errorMsg.style.display = 'block';
        return;
    }

    const elapsed = performance.now() - appState.previewLastHardRecoveryAt;
    const cooldown = appState.previewLastHardRecoveryAt > 0
        ? Math.max(0, PREVIEW_HARD_RECOVERY_COOLDOWN_MS - elapsed)
        : 0;
    const delay = Math.max(500, cooldown);
    errorMsg.innerText = `${reason}，正在重建播放器...`;
    errorMsg.style.display = 'block';

    appState.hlsRetryTimer = window.setTimeout(() => {
        appState.hlsRetryTimer = null;
        if (token !== appState.previewToken) return;
        appState.previewHardRecoveries++;
        appState.previewLastHardRecoveryAt = performance.now();
        createHlsPreviewPlayer(fullUrl, token);
    }, delay);
}

function handleHlsPreviewError(hls, video, fullUrl, token, data) {
    if (token !== appState.previewToken || hls !== appState.hlsPlayer) return;

    const detail = data?.details || 'unknown';
    const audioTimelineError = isPreviewAudioTimelineError(data);
    const immediateReset = isPreviewImmediateResetError(data);
    const severeMediaError = isPreviewSevereMediaError(data);

    if (data?.fatal && data.type === Hls.ErrorTypes.NETWORK_ERROR) {
        scheduleHlsPreviewReload(fullUrl, token, `直播网络中断 (${detail})`);
        return;
    }

    if (!data?.fatal) {
        if (detail === Hls.ErrorDetails.BUFFER_STALLED_ERROR) {
            handlePreviewPlaybackStall(hls, video, fullUrl, token);
            return;
        }

        if (audioTimelineError || immediateReset) {
            logPreviewMediaWarning('检测到音频或时间轴异常', data);
            scheduleHardPreviewRecovery(fullUrl, token, `音频时间轴异常 (${detail})`);
            return;
        }

        if (severeMediaError)
            recordPreviewSevereError(fullUrl, token, detail, data);
        return;
    }

    if (data.type === Hls.ErrorTypes.MEDIA_ERROR && !audioTimelineError && !immediateReset) {
        const now = performance.now();
        const cooldownElapsed = appState.previewLastMediaRecoveryAt === 0 ||
            now - appState.previewLastMediaRecoveryAt >= PREVIEW_MEDIA_RECOVERY_COOLDOWN_MS;
        if (appState.previewMediaRecoveries < PREVIEW_MAX_MEDIA_RECOVERIES && cooldownElapsed) {
            appState.previewMediaRecoveries++;
            appState.previewLastMediaRecoveryAt = now;
            const errorMsg = document.getElementById('player-error-msg');
            errorMsg.innerText = `媒体解码异常，正在重置 MediaSource (${detail})...`;
            errorMsg.style.display = 'block';
            hls.recoverMediaError();
            return;
        }
    }

    logPreviewMediaWarning('检测到致命媒体异常', data);
    scheduleHardPreviewRecovery(fullUrl, token, `播放解码异常 (${detail})`);
}

function isPreviewAudioTimelineError(data) {
    const message = [
        data?.parent,
        data?.sourceBufferName,
        data?.details,
        data?.reason,
        data?.error?.message,
        data?.err?.message
    ].filter(Boolean).join(' ').toLowerCase();

    return /(?:\baudio\b|\baac\b|mp4a|sample[ -]?rate|non.?monoton|timestamp|\bpts\b|\bdts\b)/i.test(message);
}

function isPreviewImmediateResetError(data) {
    return data?.type === Hls.ErrorTypes.MUX_ERROR || [
        Hls.ErrorDetails.BUFFER_INCOMPATIBLE_CODECS_ERROR,
        Hls.ErrorDetails.MEDIA_SOURCE_REQUIRES_RESET,
        Hls.ErrorDetails.ATTACH_MEDIA_ERROR
    ].filter(Boolean).includes(data?.details);
}

function isPreviewSevereMediaError(data) {
    return [
        Hls.ErrorDetails.FRAG_PARSING_ERROR,
        Hls.ErrorDetails.BUFFER_ADD_CODEC_ERROR,
        Hls.ErrorDetails.BUFFER_APPEND_ERROR,
        Hls.ErrorDetails.BUFFER_APPENDING_ERROR,
        Hls.ErrorDetails.BUFFER_APPEND_NO_PROGRESS,
        Hls.ErrorDetails.BUFFER_FULL_ERROR
    ].filter(Boolean).includes(data?.details);
}

function recordPreviewSevereError(fullUrl, token, detail, data) {
    const now = performance.now();
    if (now - appState.previewLastSevereErrorAt > PREVIEW_SEVERE_ERROR_WINDOW_MS)
        appState.previewSevereErrorCount = 0;

    appState.previewLastSevereErrorAt = now;
    appState.previewSevereErrorCount++;
    logPreviewMediaWarning('检测到媒体缓冲异常', data);

    if (appState.previewSevereErrorCount >= 2)
        scheduleHardPreviewRecovery(fullUrl, token, `媒体缓冲持续异常 (${detail})`);
}

function logPreviewMediaWarning(message, data) {
    console.warn(`[试播] ${message}`, {
        type: data?.type,
        details: data?.details,
        reason: data?.reason || data?.error?.message,
        parent: data?.parent,
        sourceBufferName: data?.sourceBufferName,
        sequence: data?.frag?.sn,
        continuity: data?.frag?.cc
    });
}

function detectPreviewAudioCodecChange(data, fullUrl, token) {
    const track = data?.audio || data?.tracks?.audio;
    if (!track) return;

    const signatureParts = [track.container || '', track.codec || track.levelCodec || ''];
    if (!signatureParts.some(Boolean)) return;
    const signature = signatureParts.join('|');

    if (appState.previewAudioCodecSignature && appState.previewAudioCodecSignature !== signature) {
        console.warn('[试播] 音频编解码参数在播放期间发生变化', {
            previous: appState.previewAudioCodecSignature,
            current: signature
        });
        scheduleHardPreviewRecovery(fullUrl, token, '音频编解码参数发生变化');
    }
    appState.previewAudioCodecSignature = signature;
}

function detectPreviewTimestampDrift(data, fullUrl, token) {
    if (appState.previewBufferedFragments < 2) return;

    const drift = Math.abs(Number(data?.drift));
    const continuity = data?.frag?.cc;
    const sameContinuity = continuity != null && continuity === appState.previewLastFragContinuity;
    if (Number.isFinite(drift) && drift > PREVIEW_TIMESTAMP_GAP_SECONDS && sameContinuity) {
        console.warn('[试播] 检测到未标记的时间戳漂移', {
            drift,
            sequence: data?.frag?.sn,
            continuity
        });
        scheduleHardPreviewRecovery(fullUrl, token, `媒体时间戳异常漂移 (${drift.toFixed(2)}s)`);
    }
}

function inspectPreviewFragmentTimeline(data, fullUrl, token) {
    const frag = data?.frag;
    if (!frag || (data?.id && data.id !== 'main')) return;

    const sequence = Number(frag.sn);
    const continuity = frag.cc;
    const parsedStartPts = toFinitePreviewNumber(frag.startPTS);
    const startPts = parsedStartPts ?? toFinitePreviewNumber(frag.start);
    const duration = Number(frag.duration);
    const parsedEndPts = toFinitePreviewNumber(frag.endPTS);
    const endPts = parsedEndPts ?? (Number.isFinite(startPts) && Number.isFinite(duration)
        ? startPts + duration
        : null);
    const sequential = Number.isFinite(sequence) && Number.isFinite(appState.previewLastFragSequence) &&
        sequence === appState.previewLastFragSequence + 1;
    const sameContinuity = continuity != null && continuity === appState.previewLastFragContinuity;

    if (sequential && sameContinuity && Number.isFinite(startPts) && Number.isFinite(appState.previewLastFragEndPts)) {
        const gap = startPts - appState.previewLastFragEndPts;
        if (Math.abs(gap) > PREVIEW_TIMESTAMP_GAP_SECONDS) {
            console.warn('[试播] 相邻分片时间轴不连续', { gap, sequence, continuity });
            scheduleHardPreviewRecovery(fullUrl, token, `相邻分片时间轴异常 (${gap.toFixed(2)}s)`);
        }
    }

    appState.previewBufferedFragments++;
    appState.previewLastFragSequence = Number.isFinite(sequence) ? sequence : null;
    appState.previewLastFragContinuity = continuity ?? null;
    appState.previewLastFragEndPts = Number.isFinite(endPts) ? endPts : null;
}

function toFinitePreviewNumber(value) {
    if (value == null || value === '') return null;
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
}

function resetPreviewGenerationState() {
    appState.previewLastProgressAt = performance.now();
    appState.previewLastTime = 0;
    appState.previewLastSoftRecoveryAt = 0;
    appState.previewSevereErrorCount = 0;
    appState.previewLastSevereErrorAt = 0;
    appState.previewBufferedFragments = 0;
    appState.previewLastFragEndPts = null;
    appState.previewLastFragSequence = null;
    appState.previewLastFragContinuity = null;
    appState.previewAudioCodecSignature = null;
}

function bindPreviewMediaProgressEvents(video, token) {
    const markProgress = () => {
        if (token !== appState.previewToken || video.paused) return;
        const currentTime = video.currentTime;
        if (!Number.isFinite(currentTime)) return;

        if (currentTime > appState.previewLastTime + 0.05 || currentTime < appState.previewLastTime - 1) {
            appState.previewLastTime = currentTime;
            appState.previewLastProgressAt = performance.now();
        }
    };

    video.ontimeupdate = markProgress;
    video.onplaying = () => {
        if (token !== appState.previewToken) return;
        appState.previewLastTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
        appState.previewLastProgressAt = performance.now();
    };
}

function handlePreviewPlaybackStall(hls, video, fullUrl, token) {
    if (!hls || !video || video.paused || token !== appState.previewToken) return;

    const now = performance.now();
    const stalledFor = now - appState.previewLastProgressAt;
    if (stalledFor >= PREVIEW_SOFT_STALL_RECOVERY_MS &&
        now - appState.previewLastSoftRecoveryAt >= PREVIEW_SOFT_STALL_RECOVERY_MS) {
        appState.previewLastSoftRecoveryAt = now;
        recoverHlsPreviewToLiveEdge(hls, video);
    }

    if (stalledFor >= PREVIEW_HARD_STALL_RECOVERY_MS)
        scheduleHardPreviewRecovery(fullUrl, token, '播放持续停滞');
}

function recoverHlsPreviewToLiveEdge(hls, video) {
    if (!hls || !video || video.paused) return;

    const livePosition = hls.liveSyncPosition;
    if (Number.isFinite(livePosition) && livePosition - video.currentTime > 12) {
        try { video.currentTime = Math.max(0, livePosition - 1); } catch (_) {}
    }

    try { hls.startLoad(); } catch (_) {}
    video.play().catch(() => {});
}

function recoverNativePreview(video) {
    if (!video || video.paused) return;
    try {
        if (video.seekable.length > 0) {
            const liveEdge = video.seekable.end(video.seekable.length - 1);
            if (liveEdge - video.currentTime > 12)
                video.currentTime = Math.max(0, liveEdge - 1);
        }
        video.play().catch(() => {});
    } catch (_) {}
}

function startPreviewStallMonitor(token) {
    window.clearInterval(appState.hlsStallTimer);
    appState.hlsStallTimer = window.setInterval(() => {
        if (token !== appState.previewToken) return;
        const video = document.getElementById('preview-video');
        const hls = appState.hlsPlayer;
        if (!video || !hls || video.paused) return;

        if (video.currentTime > appState.previewLastTime + 0.1) {
            appState.previewLastTime = video.currentTime;
            appState.previewLastProgressAt = performance.now();
            return;
        }

        handlePreviewPlaybackStall(hls, video, appState.previewUrl, token);
    }, 2000);
}

function destroyHlsPreviewInstance(video) {
    const previousHls = appState.hlsPlayer;
    // 先清除全局引用，使 destroy() 期间同步触发的旧代事件也会被代际校验拒绝。
    appState.hlsPlayer = null;
    if (previousHls) {
        previousHls.destroy();
    }

    if (!video) return;
    video.onloadedmetadata = null;
    video.onstalled = null;
    video.onerror = null;
    video.ontimeupdate = null;
    video.onplaying = null;
    video.pause();
    video.removeAttribute('src');
    video.load();
}

function resetPreviewPlayback() {
    window.clearTimeout(appState.hlsRetryTimer);
    window.clearInterval(appState.hlsStallTimer);
    appState.hlsRetryTimer = null;
    appState.hlsStallTimer = null;
    destroyHlsPreviewInstance(document.getElementById('preview-video'));
}

function closePreviewModal() {
    appState.previewToken++;
    appState.previewUrl = null;
    resetPreviewPlayback();
    closeModal('modal-preview');
}

// -------------------------------------------------------------
// Utilities & Modals
// -------------------------------------------------------------

function openModal(id) {
    document.getElementById(id)?.classList.add('active');
}

function closeModal(id) {
    document.getElementById(id)?.classList.remove('active');
}

async function copyTextToClipboard(text, label = '内容', buttonEl = null) {
    if (!text) return;
    let success = false;

    // 1. 尝试现代 Clipboard API (HTTPS 或 localhost 环境)
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            success = true;
        } catch (err) {
            console.warn('navigator.clipboard.writeText 失败，尝试降级兼容方案:', err);
        }
    }

    // 2. 兼容降级方案 (适用于纯 HTTP 局域网 IP 如 192.168.x.x 等环境)
    if (!success) {
        try {
            const textArea = document.createElement('textarea');
            textArea.value = text;
            textArea.style.position = 'fixed';
            textArea.style.left = '-9999px';
            textArea.style.top = '-9999px';
            textArea.setAttribute('readonly', '');
            document.body.appendChild(textArea);
            textArea.select();
            textArea.setSelectionRange(0, 99999);
            success = document.execCommand('copy');
            document.body.removeChild(textArea);
        } catch (err) {
            console.error('execCommand 复制失败:', err);
        }
    }

    if (success) {
        showToast(`${label}已复制到剪贴板！`, 'success');
        if (buttonEl) {
            const originalHtml = buttonEl.innerHTML;
            const originalClass = buttonEl.className;
            buttonEl.classList.add('btn-copied-success');
            buttonEl.innerHTML = `
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.5">
                    <polyline points="20 6 9 17 4 12"></polyline>
                </svg>
                <span>已复制！</span>
            `;
            setTimeout(() => {
                buttonEl.innerHTML = originalHtml;
                buttonEl.className = originalClass;
            }, 2000);
        }
    } else {
        // 极端受限环境下的终极弹窗复制引导
        prompt(`无法直接写入剪贴板，请按 Ctrl+C / Cmd+C 复制 ${label}：`, text);
    }
}

async function detectBrowserCandidateIps() {
    return new Promise((resolve) => {
        const ips = new Set();
        try {
            const RTCPeer = window.RTCPeerConnection || window.webkitRTCPeerConnection || window.mozRTCPeerConnection;
            if (!RTCPeer) return resolve([]);
            const pc = new RTCPeer({ iceServers: [] });
            pc.createDataChannel('');
            pc.createOffer().then(offer => pc.setLocalDescription(offer)).catch(() => {});
            pc.onicecandidate = (ice) => {
                if (!ice || !ice.candidate || !ice.candidate.candidate) return;
                const match = /([0-9]{1,3}(\.[0-9]{1,3}){3})/.exec(ice.candidate.candidate);
                if (match && match[1]) {
                    const ip = match[1];
                    if (!ip.startsWith('127.') && !ip.startsWith('172.17.') && !ip.startsWith('172.18.') && !ip.startsWith('172.20.')) {
                        ips.add(ip);
                    }
                }
            };
            setTimeout(() => {
                try { pc.close(); } catch (_) {}
                resolve(Array.from(ips));
            }, 600);
        } catch (_) {
            resolve([]);
        }
    });
}

async function openHostModal(defaultFill = '') {
    const input = document.getElementById('input-custom-host');
    const hintBox = document.getElementById('host-candidate-box');
    
    if (input) {
        input.value = appState.config?.customHost || defaultFill || '';
    }

    openModal('modal-host');

    // 自动检测本机局域网候选 IP 并在下方显示一键填充标签
    if (hintBox) {
        hintBox.innerHTML = '<span>🔍 正在探测本机局域网 IP...</span>';
        const detected = await detectBrowserCandidateIps();
        if (detected.length > 0) {
            hintBox.innerHTML = `
                <div style="margin-top: 8px;">
                    <span>💡 检测到本机可能 IP：</span>
                    ${detected.map(ip => `
                        <button type="button" class="btn btn-sm btn-secondary" style="padding: 2px 8px; font-size: 11px; margin: 2px 4px 2px 0;" onclick="selectCandidateHost('${ip}')">
                            ${ip}
                        </button>
                    `).join('')}
                </div>
            `;
            if (input && !input.value) {
                input.value = detected[0];
            }
        } else {
            hintBox.innerHTML = `<span>💡 提示：输入当前电脑/NAS的局域网 IP (如 192.168.10.2)，局域网其它设备即可直连。</span>`;
        }
    }
}

function selectCandidateHost(ip) {
    const input = document.getElementById('input-custom-host');
    if (input) {
        input.value = ip;
        input.focus();
    }
}

async function saveCustomHost() {
    const input = document.getElementById('input-custom-host');
    const val = input ? input.value.trim() : '';
    const btn = document.getElementById('btn-save-host');

    if (btn) {
        btn.disabled = true;
        btn.innerText = '保存中...';
    }

    try {
        const res = await apiFetch('/api/config/host', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ customHost: val })
        });

        if (res.ok) {
            if (!appState.config) appState.config = {};
            appState.config.customHost = val;
            closeModal('modal-host');
            await loadStatus(false);
            showToast(val ? `已配置局域网主机: ${val}` : '已恢复自动识别主机', 'success');
        } else {
            showToast('保存失败，请稍后重试', 'error');
        }
    } catch (err) {
        showToast('保存异常: ' + err.message, 'error');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerText = '保存生效';
        }
    }
}

async function copyM3uUrl() {
    let url = appState.status?.m3uUrl || '';

    if (!url) {
        openPlaybackSecurityModal();
        showToast('请先生成或恢复独立播放令牌', 'warning');
        return;
    }
    
    // 如果当前通过 localhost / 127.0.0.1 访问，且配置中未设置 customHost，弹窗引导用户快速确认真实局域网 IP
    const isLocalhost = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const hasCustomHost = appState.config?.customHost && appState.config.customHost.trim().length > 0;
    
    if (isLocalhost && !hasCustomHost && (url.includes('localhost') || url.includes('127.0.0.1'))) {
        const detected = await detectBrowserCandidateIps();
        const candidateIp = detected.length > 0 ? detected[0] : '';
        openHostModal(candidateIp);
        showToast('💡 请先确认/填写宿主机真实局域网 IP，以供其他设备访问', 'info');
        return;
    }

    const btn = document.getElementById('btn-copy-m3u');
    copyTextToClipboard(url, 'M3U 订阅源地址', btn);
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.innerHTML = `
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2">
            ${type === 'success' ? '<polyline points="20 6 9 17 4 12"></polyline>' : '<circle cx="12" cy="12" r="10"></circle><line x1="12" y1="8" x2="12" y2="12"></line><line x1="12" y1="16" x2="12.01" y2="16"></line>'}
        </svg>
        <span>${escapeHtml(message)}</span>
    `;

    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(15px)';
        toast.style.transition = 'all 0.25s ease';
        setTimeout(() => toast.remove(), 250);
    }, 3000);
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

// 统一风格自定义确认对话框 (替代原生 confirm)
function showConfirm({
    title = '确认操作',
    message = '确定要执行此操作吗？',
    okText = '确定',
    cancelText = '取消',
    type = 'danger' // 'danger' | 'warning' | 'primary'
} = {}) {
    return new Promise((resolve) => {
        const titleEl = document.getElementById('confirm-title');
        const msgEl = document.getElementById('confirm-message');
        const okBtn = document.getElementById('confirm-btn-ok');
        const cancelBtn = document.getElementById('confirm-btn-cancel');
        const iconBox = document.getElementById('confirm-icon-box');

        if (titleEl) titleEl.innerText = title;
        if (msgEl) msgEl.innerText = message;
        if (okBtn) okBtn.innerText = okText;
        if (cancelBtn) cancelBtn.innerText = cancelText;

        if (okBtn && iconBox) {
            if (type === 'danger') {
                okBtn.className = 'btn btn-danger';
                iconBox.className = 'confirm-icon-box danger';
            } else if (type === 'warning') {
                okBtn.className = 'btn btn-warning';
                iconBox.className = 'confirm-icon-box warning';
            } else {
                okBtn.className = 'btn btn-primary';
                iconBox.className = 'confirm-icon-box primary';
            }
        }

        const handleOk = () => {
            cleanup();
            resolve(true);
        };

        const handleCancel = () => {
            cleanup();
            resolve(false);
        };

        const handleKeydown = (e) => {
            if (e.key === 'Escape') handleCancel();
        };

        const cleanup = () => {
            okBtn?.removeEventListener('click', handleOk);
            cancelBtn?.removeEventListener('click', handleCancel);
            document.removeEventListener('keydown', handleKeydown);
            closeModal('modal-confirm');
        };

        okBtn?.addEventListener('click', handleOk, { once: true });
        cancelBtn?.addEventListener('click', handleCancel, { once: true });
        document.addEventListener('keydown', handleKeydown);

        openModal('modal-confirm');
        okBtn?.focus();
    });
}
