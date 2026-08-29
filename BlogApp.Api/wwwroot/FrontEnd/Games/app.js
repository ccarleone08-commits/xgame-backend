// ==================== GLOBAL STATE ====================
let connection = null;
let currentUser = null;
let currentRoom = null;
let playerHand = [];
let selectedTile = null;
let gameTimer = null;
let roomRefreshInterval = null;

// ==================== INITIALIZATION ====================
document.addEventListener('DOMContentLoaded', () => {
    log('Initializing Okey Game...');

    // Hide loading screen
    setTimeout(() => {
        document.getElementById('loadingScreen').classList.add('hidden');
    }, 1000);

    // Check if user is logged in (check cookie)
    const token = getToken();
    if (token) {
        showScreen('roomListScreen');
        initializeSignalR();
    } else {
        showScreen('loginScreen');
    }

    // Setup event listeners
    setupEventListeners();
});

// ==================== SCREEN MANAGEMENT ====================
function showScreen(screenId) {
    document.querySelectorAll('.screen').forEach(screen => {
        screen.classList.remove('active');
    });
    document.getElementById(screenId).classList.add('active');
    log(`Switched to screen: ${screenId}`);
}

// ==================== EVENT LISTENERS ====================
function setupEventListeners() {
    // Login
    document.getElementById('loginBtn').addEventListener('click', handleLogin);
    document.getElementById('passwordInput').addEventListener('keypress', (e) => {
        if (e.key === 'Enter') handleLogin();
    });

    // Logout
    document.getElementById('logoutBtn').addEventListener('click', handleLogout);

    // Game Actions
    document.getElementById('drawStockBtn').addEventListener('click', () => drawTile('stock'));
    document.getElementById('drawDiscardBtn').addEventListener('click', () => drawTile('discard'));
    document.getElementById('declareWinBtn').addEventListener('click', handleDeclareWin);
    document.getElementById('hintBtn').addEventListener('click', handleRequestHint);
    document.getElementById('leaveRoomBtn').addEventListener('click', handleLeaveRoom);

    // Quick Messages - Setup once on load
    setupQuickMessages();

    // Game Over Modal
    document.getElementById('backToLobbyBtn').addEventListener('click', () => {
        document.getElementById('gameOverModal').classList.remove('active');
        handleLeaveRoom();
    });

    // Stock pile click
    document.getElementById('stockPile').addEventListener('click', () => {
        if (!document.getElementById('drawStockBtn').disabled) {
            drawTile('stock');
        }
    });
}

// ==================== QUICK MESSAGES SETUP ====================
function setupQuickMessages() {
    console.log('Setting up quick messages...');

    // Click on quick message icons (per player)
    document.addEventListener('click', (e) => {
        if (e.target.classList.contains('quick-msg-icon')) {
            e.preventDefault();
            e.stopPropagation();
            console.log('Quick message icon clicked');
            showQuickMessagesPanel();
        }
    });

    // Close button
    const closeBtn = document.getElementById('closeQuickMsg');
    if (closeBtn) {
        closeBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            console.log('Close button clicked');
            hideQuickMessagesPanel();
        });
    }

    // Click on overlay (background) to close
    const quickPanel = document.getElementById('quickMessagesPanel');
    if (quickPanel) {
        quickPanel.addEventListener('click', (e) => {
            // Eger overlay-in ozune click edirikse (content-e deyil)
            if (e.target === quickPanel) {
                hideQuickMessagesPanel();
            }
        });
    }

    // Message buttons
    const msgButtons = document.querySelectorAll('.quick-msg-btn');
    console.log('Found message buttons:', msgButtons.length);

    msgButtons.forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const message = btn.getAttribute('data-msg');
            console.log('Message button clicked:', message);
            if (message) {
                handleSendQuickMessage(message);
                hideQuickMessagesPanel();
            }
        });
    });

    console.log('Quick messages setup complete');
}


// ==================== AUTHENTICATION ====================
async function handleLogin() {
    const username = document.getElementById('usernameInput').value.trim();
    const password = document.getElementById('passwordInput').value.trim();

    if (!username || !password) {
        showNotification('Xahiş edirik bütün xanaları doldurun', 'error');
        return;
    }

    try {
        const response = await fetch('/api/Auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, password })
        });

        if (response.ok) {
            const data = await response.json();

            currentUser = {
                id: data.userId,
                username: data.username,
                fullName: data.fullName || username,
                balance: data.balance || 5000
            };

            showNotification('Uğurla daxil oldunuz!', 'success');
            showScreen('roomListScreen');

            await initializeSignalR();
        } else {
            const error = await response.text();
            showNotification(error || 'Giriş uğursuz oldu', 'error');
        }
    } catch (error) {
        logError('Login error:', error);
        showNotification('Bağlantı xətası', 'error');
    }
}

async function handleLogout() {
    try {
        if (connection) {
            await connection.stop();
        }

        await fetch('/api/Auth/logout', { method: 'POST' });

        document.cookie = 'AuthToken=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';

        currentUser = null;
        currentRoom = null;

        showScreen('loginScreen');
        showNotification('Çıxış edildi', 'info');
    } catch (error) {
        logError('Logout error:', error);
    }
}

// ==================== TOKEN MANAGEMENT ====================
function getToken() {
    const raw = document.cookie
        .split("; ")
        .find(row => row.startsWith("AuthToken="))
        ?.split("=")[1];
    return raw ? decodeURIComponent(raw).trim() : "";
}

// ==================== SIGNALR CONNECTION ====================
async function initializeSignalR() {
    try {
        log('Connecting to SignalR Hub...');

        connection = new signalR.HubConnectionBuilder()
            .withUrl("/okeyhub", {
                accessTokenFactory: () => getToken()
            })
            .withAutomaticReconnect()
            .build();

        setupSignalRHandlers();

        await connection.start();
        log('✅ SignalR Connected!');

        await loadRoomList();
        startRoomRefresh();

    } catch (error) {
        logError('❌ SignalR connection failed:', error);
        showNotification('Bağlantı xətası! Yenidən cəhd edin', 'error');
    }
}

function setupSignalRHandlers() {
    connection.on('UserData', (data) => {
        log('User data received:', data);
        currentUser = data;
        document.getElementById('userName').textContent = data.fullName;
        document.getElementById('userBalance').innerHTML = `<span class="coin-icon">💰</span> ${data.balance}`;
    });

    connection.on('RoomCreated', (room) => {
        log('Room created:', room);
        loadRoomList();
    });

    connection.on('RoomDeleted', (roomId) => {
        log('Room deleted:', roomId);
        loadRoomList();
    });

    connection.on('JoinedRoom', async (data) => {
        log('Joined room:', data);
        currentRoom = {
            roomId: data.roomId,
            roomName: data.roomName,
            isGameStarted: data.isGameStarted
        };
        playerHand = data.hand || [];

        document.getElementById('roomNameDisplay').textContent = data.roomName;

        if (data.balance !== undefined) {
            currentUser.balance = data.balance;
            document.getElementById('userBalance').innerHTML = `<span class="coin-icon">💰</span> ${data.balance}`;
        }

        showScreen('gameScreen');
        renderPlayerHand();

        if (data.isGameStarted && data.gameState) {
            updateGameState(data.gameState);
        }
    });
    connection.onclose(async (error) => {
        log('SignalR connection closed', error);

        if (currentRoom && !isDisconnecting) {
            isDisconnecting = true;
            showNotification('Bağlantı kəsildi! Otaqdan çıxırıq...', 'error');

            // Clear current room state
            currentRoom = null;
            playerHand = [];

            // Go back to login if not authenticated
            const token = getToken();
            if (!token) {
                showScreen('loginScreen');
            } else {
                showScreen('roomListScreen');
                // Try to reconnect
                setTimeout(() => {
                    initializeSignalR();
                }, 2000);
            }

            isDisconnecting = false;
        }
    });

    // Reconnecting handler
    connection.onreconnecting((error) => {
        log('Reconnecting...', error);
        showNotification('Yenidən bağlanılır...', 'info');
    });

    // Reconnected handler
    connection.onreconnected((connectionId) => {
        log('Reconnected!', connectionId);
        showNotification('Bağlantı bərpa olundu!', 'success');

        // If we were in a room, user needs to rejoin manually
        if (currentRoom) {
            showNotification('Zəhmət olmasa yenidən otağa qoşulun', 'info');
            currentRoom = null;
            showScreen('roomListScreen');
            loadRoomList();
        }
    });

    connection.on('JoinError', (message) => {
        log('Join error:', message);
        showNotification(message, 'error');
    });

    connection.on('PlayerJoined', (data) => {
        log('Player joined:', data);
        showNotification(`${data.playerName} otağa qoşuldu`, 'info');
        showFloatingMessage(data.playerName, '👋 Qoşuldu');
    });

    connection.on('PlayerLeft', (data) => {
        log('Player left:', data);
        showNotification(`${data.playerName} otaqdan çıxdı`, 'info');
        showFloatingMessage(data.playerName, '👋 Çıxdı');
    });

    connection.on('PlayersList', (players) => {
        log('Players list:', players);
        updatePlayersList(players);
    });

    connection.on('LeftRoom', () => {
        log('Left room');
        currentRoom = null;
        playerHand = [];
        showScreen('roomListScreen');
        loadRoomList();
    });

    connection.on('BalanceUpdated', (balance) => {
        log('Balance updated:', balance);
        currentUser.balance = balance;
        document.getElementById('userBalance').innerHTML = `<span class="coin-icon">💰</span> ${balance}`;
    });

    connection.on('GameStarted', (data) => {
        log('Game started:', data);
        playerHand = data.hand;
        renderPlayerHand();

        renderTile(data.indicator, document.getElementById('indicatorTile'));
        renderTile(data.joker, document.getElementById('jokerTile'));

        if (data.isYourTurn) {
            enableDrawButtons();
            startTurnTimer();
        }

        showNotification('Oyun başladı! Uğurlar!', 'success');
        showFloatingMessage('System', '🎮 Oyun başladı!');
    });

    connection.on('GameStateUpdated', (state) => {
        log('Game state updated:', state);
        updateGameState(state);
    });

    connection.on('TileDrawn', (data) => {
        log('Tile drawn:', data);
        playerHand = data.hand;
        renderPlayerHand();

        disableDrawButtons();
        enableDiscardMode();

        showNotification(`${data.source === 'stock' ? 'Dəstədən' : 'Atılmışdan'} daş çəkdiniz`, 'info');
    });

    connection.on('TileDiscarded', (data) => {
        log('Tile discarded:', data);
        playerHand = data.hand;
        renderPlayerHand();
        disableDiscardMode();
        disableDrawButtons();
        stopTurnTimer();

        if (data.discardedTile && data.playerIndex !== undefined) {
            showPlayerDiscardedTile(data.playerIndex, data.discardedTile);
        }
    });

    connection.on('PlayerDiscardedTile', (data) => {
        log('Player discarded tile:', data);
        if (data.tile && data.playerIndex !== undefined) {
            showPlayerDiscardedTile(data.playerIndex, data.tile);
            showFloatingMessage(data.playerName, '🎴 Daş atdı');
        }
    });

    connection.on('YourTurn', () => {
        log('Your turn!');
        enableDrawButtons();
        startTurnTimer();
        showNotification('Sizin növbənizdir!', 'success');
    });

    connection.on('WinDeclared', (data) => {
        log('Win declared:', data);
        if (!data.isValid) {
            showNotification(data.message, 'error');
        }
    });

    connection.on('GameOver', (data) => {
        log('Game over:', data);
        stopTurnTimer();
        showGameOver(data);
    });

    connection.on('GameReset', () => {
        log('Game reset');
        playerHand = [];
        renderPlayerHand();
        document.getElementById('indicatorTile').innerHTML = '';
        document.getElementById('jokerTile').innerHTML = '';
        document.getElementById('discardPile').innerHTML = '<div class="discard-label">Atılmış</div>';
        disableAllGameButtons();
    });

    connection.on('HintProvided', (hint) => {
        log('Hint received:', hint);
        showHint(hint);
    });

    connection.on('ChatMessage', (data) => {
        log('Chat message:', data);
        showFloatingMessage(data.username, data.message);
    });

    connection.on('Error', (message) => {
        log('Error received:', message);
        showNotification(message, 'error');
    });
}

// ==================== ROOM MANAGEMENT ====================
async function loadRoomList() {
    try {
        const rooms = await connection.invoke('GetRoomList');
        log('Room list loaded:', rooms);
        renderRoomList(rooms);
    } catch (error) {
        logError('Failed to load room list:', error);
    }
}

function renderRoomList(rooms) {
    const grid = document.getElementById('roomsGrid');
    grid.innerHTML = '';

    if (rooms.length === 0) {
        grid.innerHTML = '<div style="grid-column: 1/-1; text-align: center; color: white; padding: 40px;"><h2>Heç bir otaq yoxdur</h2><p>Yenilənir...</p></div>';
        return;
    }

    rooms.forEach(room => {
        const card = document.createElement('div');
        card.className = 'room-card';
        const remainingPlayers = Math.max(0, room.maxPlayers - room.playerCount);
        const playerCountText = room.playerCount === 0
            ? '⏳ Boş otaq'
            : remainingPlayers > 0
                ? `⏳ ${remainingPlayers} oyunçu gözləyir`
                : '✅ Tam doldu';

        if (room.playerCount >= room.maxPlayers) {
            card.classList.add('full');
        }

        const status = room.isGameStarted ? 'playing' :
            room.playerCount >= room.maxPlayers ? 'full' : 'waiting';

        const statusText = room.isGameStarted ? 'Oyun davam edir' :
            room.playerCount >= room.maxPlayers ? 'Doludur' : 'Gözləyir';

        card.innerHTML = `
            <div class="room-header">
                <div class="room-name">
                    ${room.roomName}
                </div>
                <div class="room-status ${status}">${statusText}</div>
            </div>
            <div class="room-info">
                <div class="room-detail">
                    <span>Oyunçular:</span>
                    <span>👥 ${room.playerCount}/${room.maxPlayers}</span>
                </div>
                <div class="room-detail">
                    <span>Giriş haqqı:</span>
                    <span>💰 ${room.entryFee} ₼</span>
                </div>
                <div class="room-detail">
                    <span>Rejim:</span>
                    <span>${room.gameMode === 'casual' ? 'Adi' : 'Turnir'}</span>
                </div>
            </div>
            <div class="room-players">
                <div class="player-count">
                    ${playerCountText}
                </div>
                <button class="btn btn-primary btn-join" 
                        data-room-id="${room.roomId}"
                        ${room.playerCount >= room.maxPlayers ? 'disabled' : ''}>
                    ${room.playerCount >= room.maxPlayers ? 'Doludur' : 'Qoşul'}
                </button>
            </div>
        `;

        const joinBtn = card.querySelector('.btn-join');
        if (joinBtn && !joinBtn.disabled) {
            joinBtn.addEventListener('click', () => handleJoinRoom(room.roomId));
        }

        grid.appendChild(card);
    });
}

function startRoomRefresh() {
    if (roomRefreshInterval) {
        clearInterval(roomRefreshInterval);
    }

    roomRefreshInterval = setInterval(() => {
        if (document.getElementById('roomListScreen').classList.contains('active')) {
            loadRoomList();
        }
    }, CONFIG.ROOM_REFRESH_INTERVAL);
}

async function handleJoinRoom(roomId) {
    try {
        await connection.invoke('JoinRoom', roomId, null);
    } catch (error) {
        logError('Join room error:', error);
        showNotification('Otağa qoşula bilmədiniz', 'error');
    }
}

async function handleLeaveRoom() {
    if (!currentRoom) return;

    try {
        await connection.invoke('LeaveRoom');
    } catch (error) {
        logError('Leave room error:', error);
    }
}

// ==================== GAME LOGIC ====================
function renderPlayerHand() {
    const handContainer = document.getElementById('playerHand');
    handContainer.innerHTML = '';

    const sortedHand = [...playerHand].sort((a, b) => {
        if (a.color !== b.color) {
            const colorOrder = { 'Red': 0, 'Yellow': 1, 'Blue': 2, 'Black': 3, 'FakeJoker': 4 };
            return colorOrder[a.color] - colorOrder[b.color];
        }
        return a.number - b.number;
    });

    sortedHand.forEach(tile => {
        const tileElement = createTileElement(tile);
        tileElement.addEventListener('click', () => handleTileClick(tile, tileElement));
        handContainer.appendChild(tileElement);
    });
}

function createTileElement(tile) {
    const div = document.createElement('div');
    div.className = 'okey-tile';
    div.dataset.tileId = tile.id;

    if (tile.isFakeJoker) {
        div.classList.add('fake-joker');
        div.innerHTML = '<div class="tile-number">★</div>';
    } else if (tile.isJoker) {
        div.classList.add('joker');
        div.classList.add(tile.color.toLowerCase());
        div.style.position = 'relative';
        div.innerHTML = `<div class="tile-number">${tile.number}</div><div class="tile-real-okey-badge" style="position:absolute;top:3px;right:3px;padding:1px 4px;border-radius:5px;background:#1976d2;color:white;font-size:9px;font-weight:900;line-height:1.2;">OK</div>`;
    } else {
        div.classList.add(tile.color.toLowerCase());
        div.innerHTML = `<div class="tile-number">${tile.number}</div>`;
    }

    return div;
}

function renderTile(tile, container) {
    if (!tile) return;

    container.innerHTML = '';
    const tileElement = createTileElement(tile);
    container.appendChild(tileElement);
}

function handleTileClick(tile, element) {
    if (document.getElementById('drawStockBtn').disabled &&
        !document.getElementById('declareWinBtn').disabled) {

        document.querySelectorAll('.okey-tile.selected').forEach(t => {
            t.classList.remove('selected');
        });

        element.classList.add('selected');
        selectedTile = tile;

        setTimeout(() => discardSelectedTile(), 300);
    }
}

async function drawTile(source) {
    try {
        await connection.invoke('DrawTile', source);
    } catch (error) {
        logError('Draw tile error:', error);
        showNotification('Daş çəkilə bilmədi', 'error');
    }
}

async function discardSelectedTile() {
    if (!selectedTile) {
        showNotification('Daş seçin', 'error');
        return;
    }

    try {
        await connection.invoke('DiscardTile', selectedTile.id);
        selectedTile = null;
    } catch (error) {
        logError('Discard tile error:', error);
        showNotification('Daş atıla bilmədi', 'error');
    }
}

async function handleDeclareWin() {
    try {
        await connection.invoke('DeclareWin');
    } catch (error) {
        logError('Declare win error:', error);
        showNotification('Xəta baş verdi', 'error');
    }
}

async function handleRequestHint() {
    try {
        await connection.invoke('RequestHint');
    } catch (error) {
        logError('Request hint error:', error);
    }
}

function updateGameState(state) {
    document.getElementById('stockCount').textContent = state.stockCount;

    if (state.discardPile) {
        renderTile(state.discardPile, document.getElementById('discardPile'));
    }

    if (state.indicator) {
        renderTile(state.indicator, document.getElementById('indicatorTile'));
    }
    if (state.joker) {
        renderTile(state.joker, document.getElementById('jokerTile'));
    }

    if (state.players) {
        updatePlayersList(state.players);

        document.querySelectorAll('.player-info').forEach(info => {
            info.classList.remove('active');
        });

        if (state.players[state.currentPlayerIndex]) {
            const currentPlayerName = state.players[state.currentPlayerIndex].name;
            document.querySelectorAll('.player-info').forEach(info => {
                const nameEl = info.querySelector('.player-name, .player-name-bottom');
                if (nameEl && nameEl.textContent.includes(currentPlayerName)) {
                    info.classList.add('active');
                }
            });
        }
    }
}

function updatePlayersList(players) {
    const avatars = ['🧔', '👨', '👩', '🧑', '👨‍💼', '👩‍💼', '👨‍🎓', '👩‍🎓'];

    players.forEach((player, index) => {
        const playerSlot = document.getElementById(`player${index}`);
        if (playerSlot) {
            const nameElement = playerSlot.querySelector('.player-name, .player-name-bottom');
            const tileCountElement = playerSlot.querySelector('.tile-count');
            const avatarElement = playerSlot.querySelector('.player-avatar');

            if (nameElement) {
                if (currentUser && player.name === currentUser.fullName) {
                    if (nameElement.classList.contains('player-name-bottom')) {
                        nameElement.textContent = `${player.name} (Siz)`;
                    } else {
                        nameElement.textContent = player.name;
                    }
                } else {
                    nameElement.textContent = player.name || 'Gözləyir...';
                }
            }

            if (tileCountElement) tileCountElement.textContent = `${player.handCount} daş`;

            if (avatarElement && player.name) {
                const avatarIndex = player.name.charCodeAt(0) % avatars.length;
                avatarElement.textContent = avatars[avatarIndex];
            }

            const tilesBackContainer = playerSlot.querySelector('.player-tiles-back');
            if (tilesBackContainer) {
                tilesBackContainer.style.display = 'none';
            }
        }
    });
}

function showPlayerDiscardedTile(playerIndex, tile) {
    const discardArea = document.querySelector(`.player-discard-area[data-player-index="${playerIndex}"]`);
    if (discardArea && tile) {
        discardArea.innerHTML = '';
        const tileElement = createTileElement(tile);
        tileElement.style.transform = 'scale(0.7)';
        tileElement.style.cursor = 'default';
        discardArea.classList.add('has-tile');
        discardArea.appendChild(tileElement);

        setTimeout(() => {
            discardArea.innerHTML = '';
            discardArea.classList.remove('has-tile');
        }, 2000);
    }
}

function enableDrawButtons() {
    document.getElementById('drawStockBtn').disabled = false;
    document.getElementById('drawDiscardBtn').disabled = false;
}

function disableDrawButtons() {
    document.getElementById('drawStockBtn').disabled = true;
    document.getElementById('drawDiscardBtn').disabled = true;
}

function enableDiscardMode() {
    document.getElementById('declareWinBtn').disabled = false;
}

function disableDiscardMode() {
    document.getElementById('declareWinBtn').disabled = true;
}

function disableAllGameButtons() {
    disableDrawButtons();
    disableDiscardMode();
}

// ==================== TIMER ====================
function startTurnTimer() {
    stopTurnTimer();

    let timeLeft = CONFIG.TURN_TIMER;
    document.getElementById('gameTimer').textContent = timeLeft;

    gameTimer = setInterval(() => {
        timeLeft--;
        document.getElementById('gameTimer').textContent = timeLeft;

        if (timeLeft <= 0) {
            stopTurnTimer();
            if (!document.getElementById('drawStockBtn').disabled) {
                drawTile('stock');
            } else if (playerHand.length > 0) {
                const randomTile = playerHand[Math.floor(Math.random() * playerHand.length)];
                selectedTile = randomTile;
                discardSelectedTile();
            }
        }
    }, 1000);
}

function stopTurnTimer() {
    if (gameTimer) {
        clearInterval(gameTimer);
        gameTimer = null;
    }
    document.getElementById('gameTimer').textContent = '--';
}

// ==================== GAME OVER ====================
function showGameOver(data) {
    const content = document.getElementById('gameOverContent');
    content.innerHTML = `
        <div style="text-align: center; padding: 20px;">
            <h2 style="color: #27ae60; font-size: 2em; margin-bottom: 20px;">
                🎉 ${data.winner} qazandı!
            </h2>
            <p style="font-size: 1.2em; color: #666; margin-bottom: 20px;">
                ${data.message}
            </p>
        </div>
    `;

    document.getElementById('gameOverModal').classList.add('active');
    showNotification(`${data.winner} oyunu qazandı!`, 'success');
}

// ==================== HINT ====================
function showHint(hint) {
    let message = '💡 Məsləhət:\n\n';

    if (hint.potentialSets && hint.potentialSets.length > 0) {
        message += 'Potensial setlər:\n';
        hint.potentialSets.forEach(set => {
            message += `  • ${set}\n`;
        });
    }

    if (hint.potentialRuns && hint.potentialRuns.length > 0) {
        message += '\nPotensial ardıcıllıqlar:\n';
        hint.potentialRuns.forEach(run => {
            message += `  • ${run}\n`;
        });
    }

    if (hint.advice) {
        message += `\n${hint.advice}`;
    }

    alert(message);
}

// ==================== QUICK MESSAGES ====================
function showQuickMessagesPanel() {
    console.log('showQuickMessagesPanel called');
    const panel = document.getElementById('quickMessagesPanel');
    if (panel) {
        panel.classList.add('active');
        console.log('Panel shown');
    } else {
        console.error('Quick messages panel not found!');
    }
}

function hideQuickMessagesPanel() {
    console.log('hideQuickMessagesPanel called');
    const panel = document.getElementById('quickMessagesPanel');
    if (panel) {
        panel.classList.remove('active');
        console.log('Panel hidden');
    }
}

async function handleSendQuickMessage(message) {
    console.log('Sending quick message:', message);
    if (!currentRoom) {
        console.warn('No current room');
        showNotification('Əvvəlcə oyuna qoşulun', 'error');
        return;
    }

    try {
        await connection.invoke('SendMessage', message);
        console.log('Message sent successfully');
        showNotification(`Mesaj göndərildi: ${message}`, 'success');
    } catch (error) {
        logError('Send quick message error:', error);
        showNotification('Mesaj göndərilə bilmədi', 'error');
    }
}
function showFloatingMessage(username, message) {
    const overlay = document.getElementById('messageOverlay');
    const messageDiv = document.createElement('div');
    messageDiv.className = 'floating-message';
    messageDiv.textContent = `${username}: ${message}`;

    const randomX = Math.random() * (window.innerWidth - 300) + 50;
    const randomY = Math.random() * (window.innerHeight - 200) + 100;

    messageDiv.style.left = randomX + 'px';
    messageDiv.style.top = randomY + 'px';

    overlay.appendChild(messageDiv);

    setTimeout(() => {
        messageDiv.remove();
    }, 2000);
}

// ==================== NOTIFICATIONS ====================
function showNotification(message, type = 'info') {
    const notification = document.getElementById('notification');
    notification.textContent = message;
    notification.className = `notification ${type}`;
    notification.classList.add('show');

    setTimeout(() => {
        notification.classList.remove('show');
    }, 3000);
}

// ==================== WINDOW EVENTS ====================
window.addEventListener('beforeunload', async () => {
    if (currentRoom && connection) {
        try {
            await connection.invoke('LeaveRoom');
        } catch (error) {
            console.error('Error leaving room on unload:', error);
        }
    }
});

window.addEventListener('offline', () => {
    showNotification('İnternet bağlantısı kəsildi!', 'error');

    // If in a room, leave it
    if (currentRoom) {
        currentRoom = null;
        playerHand = [];
        showScreen('roomListScreen');
        showNotification('Otaqdan çıxdınız (internet kəsildi)', 'error');
    }
});

window.addEventListener('online', () => {
    showNotification('İnternet bağlantısı bərpa olundu', 'success');

    // Reconnect SignalR if needed
    if (!connection || connection.state !== 'Connected') {
        setTimeout(() => {
            initializeSignalR();
        }, 1000);
    }
});
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        log('Page hidden');
    } else {
        log('Page visible');

        // Check connection when page becomes visible
        if (connection && connection.state !== 'Connected') {
            showNotification('Yenidən bağlanılır...', 'info');
            initializeSignalR();
        }
    }
});
