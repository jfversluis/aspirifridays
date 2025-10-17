using Microsoft.AspNetCore.SignalR;
using BingoBoard.Admin.Services;
using BingoBoard.Admin.Models;

namespace BingoBoard.Admin.Hubs
{
    /// <summary>
    /// SignalR hub for real-time bingo board communication
    /// </summary>
    public class BingoHub : Hub
    {
        private readonly IBingoService _bingoService;
        private readonly IClientConnectionService _clientService;
        private readonly ILogger<BingoHub> _logger;

        public BingoHub(
            IBingoService bingoService, 
            IClientConnectionService clientService,
            ILogger<BingoHub> logger)
        {
            _bingoService = bingoService;
            _clientService = clientService;
            _logger = logger;
        }

        /// <summary>
        /// Client requests a new bingo set
        /// </summary>
        public async Task RequestBingoSet(string? userName = null)
        {
            try
            {
                var connectionId = Context.ConnectionId;
                _logger.LogInformation("Client {ConnectionId} requested a new bingo set", connectionId);

                // Generate a new bingo set for the client
                var bingoSet = await _bingoService.GenerateRandomBingoSetAsync(connectionId);
                
                // Associate the bingo set with the client
                await _clientService.AssociateBingoSetAsync(connectionId, bingoSet.Id);

                // Send the bingo set to the requesting client
                await Clients.Caller.SendAsync("BingoSetReceived", bingoSet);

                // Notify admin of new client with bingo set
                await Clients.Others.SendAsync("ClientBingoSetGenerated", new 
                { 
                    ConnectionId = connectionId, 
                    BingoSetId = bingoSet.Id,
                    UserName = userName,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Sent bingo set {BingoSetId} to client {ConnectionId}", 
                    bingoSet.Id, connectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating bingo set for client {ConnectionId}", Context.ConnectionId);
                await Clients.Caller.SendAsync("Error", "Failed to generate bingo set");
            }
        }

        /// <summary>
        /// Client requests their existing bingo set using a persistent client ID
        /// </summary>
        public async Task RequestExistingBingoSet(string persistentClientId, string? userName = null)
        {
            try
            {
                var connectionId = Context.ConnectionId;
                _logger.LogInformation("Client {ConnectionId} requested existing bingo set for persistent ID {PersistentClientId}", 
                    connectionId, persistentClientId);

                // Try to get the existing bingo set
                var bingoSet = await _bingoService.GetClientBingoSetAsync(persistentClientId);
                
                if (bingoSet != null)
                {
                    // Map the connection ID to the persistent client ID
                    await _clientService.MapConnectionToPersistentClientAsync(connectionId, persistentClientId);
                    
                    // Update the connection mapping for this persistent client
                    await _clientService.AssociateBingoSetAsync(connectionId, bingoSet.Id);

                    // Send the existing bingo set to the client
                    await Clients.Caller.SendAsync("ExistingBingoSetReceived", bingoSet);

                    _logger.LogInformation("Sent existing bingo set {BingoSetId} to client {ConnectionId} for persistent ID {PersistentClientId}", 
                        bingoSet.Id, connectionId, persistentClientId);
                }
                else
                {
                    // No existing set found, create a new one but use the persistent client ID
                    _logger.LogInformation("No existing bingo set found for {PersistentClientId}, creating new one", 
                        persistentClientId);
                    
                    // Map the connection ID to the persistent client ID
                    await _clientService.MapConnectionToPersistentClientAsync(connectionId, persistentClientId);
                    
                    var newBingoSet = await _bingoService.GenerateRandomBingoSetAsync(persistentClientId);
                    
                    // Associate with current connection
                    await _clientService.AssociateBingoSetAsync(connectionId, newBingoSet.Id);

                    // Send the new bingo set
                    await Clients.Caller.SendAsync("BingoSetReceived", newBingoSet);

                    _logger.LogInformation("Sent new bingo set {BingoSetId} to client {ConnectionId} with persistent ID {PersistentClientId}", 
                        newBingoSet.Id, connectionId, persistentClientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving/creating bingo set for persistent client {PersistentClientId}", persistentClientId);
                await Clients.Caller.SendAsync("Error", "Failed to retrieve or create bingo set");
            }
        }

        /// <summary>
        /// Admin updates a square status for a specific client
        /// </summary>
        public async Task AdminUpdateSquare(string clientId, string squareId, bool isChecked)
        {
            try
            {
                _logger.LogInformation("Admin updating square {SquareId} to {Status} for client {ClientId}", 
                    squareId, isChecked, clientId);

                var success = await _bingoService.UpdateSquareStatusAsync(clientId, squareId, isChecked);
                
                if (success)
                {
                    // Check for win condition
                    var hasWin = await _bingoService.CheckForWinAsync(clientId);

                    // The clientId parameter might be either a persistent client ID or connection ID
                    // Try to get connection ID first (assuming it's a persistent client ID)
                    var connectionId = await _clientService.GetConnectionIdFromPersistentClientAsync(clientId);
                    
                    if (string.IsNullOrEmpty(connectionId))
                    {
                        // If no mapping found, assume clientId is already a connection ID
                        connectionId = clientId;
                    }

                    // Notify the specific client about the update
                    await Clients.Client(connectionId).SendAsync("SquareUpdated", new 
                    { 
                        SquareId = squareId, 
                        IsChecked = isChecked,
                        HasWin = hasWin,
                        Timestamp = DateTime.UtcNow
                    });

                    _logger.LogInformation("Notified client {ConnectionId} about admin square update for {SquareId}", 
                        connectionId, squareId);

                    // Notify all admin clients about the update
                    await Clients.Others.SendAsync("AdminSquareUpdate", new 
                    { 
                        ClientId = clientId,
                        SquareId = squareId, 
                        IsChecked = isChecked,
                        HasWin = hasWin,
                        Timestamp = DateTime.UtcNow
                    });

                    if (hasWin)
                    {
                        _logger.LogInformation("Client {ClientId} achieved bingo!", clientId);
                        await Clients.All.SendAsync("BingoAchieved", new { ClientId = clientId });
                    }
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", "Failed to update square");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating square for admin");
                await Clients.Caller.SendAsync("Error", "Failed to update square");
            }
        }

        /// <summary>
        /// Client requests approval to mark a square
        /// </summary>
        public async Task RequestSquareApproval(string squareId, bool requestedState)
        {
            try
            {
                var connectionId = Context.ConnectionId;
                
                // Get the persistent client ID for this connection
                var persistentClientId = await _clientService.GetPersistentClientIdAsync(connectionId);
                if (string.IsNullOrEmpty(persistentClientId))
                {
                    _logger.LogError("No persistent client ID found for connection {ConnectionId}", connectionId);
                    await Clients.Caller.SendAsync("Error", "Client not properly registered");
                    return;
                }
                
                _logger.LogInformation("Client {ConnectionId} (persistent: {PersistentClientId}) requesting approval for square {SquareId} to {Status}", 
                    connectionId, persistentClientId, squareId, requestedState);

                // Handle the request based on current mode (live vs free play)
                var (needsApproval, approvalId) = await _bingoService.HandleSquareRequestAsync(persistentClientId, squareId, requestedState);
                
                // Update client activity
                await _clientService.UpdateClientActivityAsync(connectionId);

                // Get the square details for notification
                var allSquares = await _bingoService.GetAllSquaresAsync();
                var square = allSquares.FirstOrDefault(s => s.Id == squareId);
                var squareLabel = square?.Label ?? squareId;

                if (!needsApproval)
                {
                    // Free play mode - square was updated directly
                    await Clients.Caller.SendAsync("SquareUpdateConfirmed", new 
                    { 
                        SquareId = squareId,
                        IsChecked = requestedState,
                        Message = $"'{squareLabel}' has been {(requestedState ? "checked" : "unchecked")} (Free Play Mode)",
                        Timestamp = DateTime.UtcNow
                    });

                    // Broadcast to all clients that this square was updated
                    await Clients.All.SendAsync("GlobalSquareUpdate", new 
                    { 
                        SquareId = squareId, 
                        IsChecked = requestedState,
                        Timestamp = DateTime.UtcNow,
                        Message = $"'{squareLabel}' has been {(requestedState ? "checked" : "unchecked")} (Free Play Mode)"
                    });

                    _logger.LogInformation("Free play mode: Updated square {SquareId} for client {ConnectionId}", 
                        squareId, connectionId);
                }
                else
                {
                    // Live mode - approval workflow as before
                    // First check if the square is already in the requested state globally
                    var globallyCheckedSquares = await _bingoService.GetGloballyCheckedSquaresAsync();
                    bool isCurrentlyChecked = globallyCheckedSquares.Contains(squareId);

                    // If the square is already in the requested state, auto-approve silently
                    if (isCurrentlyChecked == requestedState)
                    {
                        _logger.LogInformation("Auto-approving request for square {SquareId} as it's already in the requested state {RequestedState}", 
                            squareId, requestedState);

                        // Notify the client that their request has been auto-approved
                        await Clients.Caller.SendAsync("ApprovalRequestApproved", new 
                        { 
                            ApprovalId = "auto-approved",
                            SquareId = squareId,
                            SquareLabel = squareLabel,
                            NewState = requestedState,
                            Message = $"Your request to {(requestedState ? "check" : "uncheck")} '{squareLabel}' was automatically approved (already in requested state)!",
                            Timestamp = DateTime.UtcNow
                        });

                        _logger.LogInformation("Auto-approved request for client {ConnectionId} for square {SquareId}", 
                            connectionId, squareId);
                        return;
                    }

                    // Square is not in the requested state, use the returned approval ID
                    // Confirm request submission to the client
                    await Clients.Caller.SendAsync("ApprovalRequestSubmitted", new 
                    { 
                        ApprovalId = approvalId,
                        SquareId = squareId,
                        RequestedState = requestedState,
                        Message = $"Request to {(requestedState ? "check" : "uncheck")} '{squareLabel}' has been submitted for admin approval",
                        Timestamp = DateTime.UtcNow
                    });

                    // Notify admin clients about the new approval request
                    await Clients.Others.SendAsync("NewApprovalRequest", new 
                    { 
                        ApprovalId = approvalId,
                        ClientId = connectionId,
                        SquareId = squareId,
                        SquareLabel = squareLabel,
                        RequestedState = requestedState,
                        Timestamp = DateTime.UtcNow
                    });

                    _logger.LogInformation("Created approval request {ApprovalId} for client {ConnectionId}", 
                        approvalId, connectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating approval request for client {ConnectionId}", Context.ConnectionId);
                await Clients.Caller.SendAsync("Error", "Failed to submit approval request");
            }
        }

        /// <summary>
        /// Get current bingo set for a client
        /// </summary>
        public async Task GetCurrentBingoSet()
        {
            try
            {
                var connectionId = Context.ConnectionId;
                var bingoSet = await _bingoService.GetClientBingoSetAsync(connectionId);
                
                if (bingoSet != null)
                {
                    await Clients.Caller.SendAsync("CurrentBingoSet", bingoSet);
                }
                else
                {
                    await Clients.Caller.SendAsync("NoBingoSet");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving bingo set for client {ConnectionId}", Context.ConnectionId);
                await Clients.Caller.SendAsync("Error", "Failed to retrieve bingo set");
            }
        }

        /// <summary>
        /// Admin requests list of all connected clients
        /// </summary>
        public async Task GetConnectedClients()
        {
            try
            {
                var clients = await _clientService.GetAllClientsAsync();
                await Clients.Caller.SendAsync("ConnectedClientsList", clients);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving connected clients");
                await Clients.Caller.SendAsync("Error", "Failed to retrieve connected clients");
            }
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                var connectionId = Context.ConnectionId;
                var httpContext = Context.GetHttpContext();
                
                var client = new ConnectedClient
                {
                    ConnectionId = connectionId,
                    IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
                };

                await _clientService.AddClientAsync(client);
                
                // Notify all clients about the new connection
                await Clients.All.SendAsync("UserConnected", new 
                { 
                    ConnectionId = connectionId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Client {ConnectionId} connected", connectionId);
                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client connection");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                var connectionId = Context.ConnectionId;
                
                await _clientService.RemoveClientAsync(connectionId);
                
                // Notify all clients about the disconnection
                await Clients.All.SendAsync("UserDisconnected", new 
                { 
                    ConnectionId = connectionId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Client {ConnectionId} disconnected", connectionId);
                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client disconnection");
            }
        }

        /// <summary>
        /// Admin checks a square globally for all clients
        /// </summary>
        public async Task AdminCheckSquareGlobally(string squareId, bool isChecked)
        {
            try
            {
                _logger.LogInformation("Admin checking square {SquareId} globally to {Status}", 
                    squareId, isChecked);

                var success = await _bingoService.UpdateSquareGloballyAsync(squareId, isChecked);
                
                if (success)
                {
                    // Get the square label for a better message
                    var allSquares = await _bingoService.GetAllSquaresAsync();
                    var square = allSquares.FirstOrDefault(s => s.Id == squareId);
                    var squareLabel = square?.Label ?? squareId;

                    // Notify all clients about the global square update
                    await Clients.All.SendAsync("GlobalSquareUpdate", new 
                    { 
                        SquareId = squareId, 
                        IsChecked = isChecked,
                        Timestamp = DateTime.UtcNow,
                        Message = $"'{squareLabel}' has been {(isChecked ? "checked" : "unchecked")} by admin"
                    });

                    _logger.LogInformation("Global square update sent for {SquareId} ({SquareLabel})", squareId, squareLabel);
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", "Failed to update square globally");
                    _logger.LogWarning("Failed to update square {SquareId} globally", squareId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating square globally");
                await Clients.Caller.SendAsync("Error", "Failed to update square globally");
            }
        }

        /// <summary>
        /// Get all available squares for admin management
        /// </summary>
        public async Task GetAllAvailableSquares()
        {
            try
            {
                var squares = await _bingoService.GetAllSquaresAsync();
                await Clients.Caller.SendAsync("AllSquaresReceived", squares);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all squares");
                await Clients.Caller.SendAsync("Error", "Failed to retrieve squares");
            }
        }

        #region Legacy Methods (for backward compatibility)
        
        public async Task JoinGroup(string groupName)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            await Clients.Group(groupName).SendAsync("UserJoined", Context.ConnectionId);
        }

        public async Task LeaveGroup(string groupName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            await Clients.Group(groupName).SendAsync("UserLeft", Context.ConnectionId);
        }

        public async Task SendMessageToGroup(string groupName, string user, string message)
        {
            await Clients.Group(groupName).SendAsync("ReceiveMessage", user, message);
        }

        public async Task SendBingoUpdate(string groupName, object bingoData)
        {
            await Clients.Group(groupName).SendAsync("BingoUpdate", bingoData);
        }

        public async Task BroadcastToAll(string message, object data)
        {
            await Clients.All.SendAsync("BroadcastMessage", message, data);
        }

        #endregion

        /// <summary>
        /// Admin approves a square marking request
        /// </summary>
        public async Task ApproveSquareRequest(string approvalId)
        {
            try
            {
                var adminId = Context.ConnectionId; // In a real app, you'd get this from authentication
                _logger.LogInformation("Admin {AdminId} approving request {ApprovalId}", adminId, approvalId);

                // Get the approval details before processing
                var approval = await _bingoService.GetPendingApprovalAsync(approvalId);
                if (approval == null)
                {
                    await Clients.Caller.SendAsync("Error", "Approval request not found");
                    return;
                }

                // Get all related pending approvals before processing
                var allPendingApprovals = await _bingoService.GetPendingApprovalsAsync();
                var relatedApprovals = allPendingApprovals
                    .Where(a => a.SquareId == approval.SquareId && 
                               a.RequestedState == approval.RequestedState && 
                               a.Status == ApprovalStatus.Pending)
                    .ToList();

                var success = await _bingoService.ApproveSquareRequestAsync(approvalId, adminId);
                
                if (success)
                {
                    // Get the square details for notification
                    var allSquares = await _bingoService.GetAllSquaresAsync();
                    var square = allSquares.FirstOrDefault(s => s.Id == approval.SquareId);
                    var squareLabel = square?.Label ?? approval.SquareId;

                    // Notify all clients who had related approval requests
                    foreach (var relatedApproval in relatedApprovals)
                    {
                        // Get the current connection ID for this persistent client ID
                        var connectionId = await _clientService.GetConnectionIdFromPersistentClientAsync(relatedApproval.ClientId);
                        
                        if (!string.IsNullOrEmpty(connectionId))
                        {
                            await Clients.Client(connectionId).SendAsync("ApprovalRequestApproved", new 
                            { 
                                ApprovalId = relatedApproval.Id,
                                SquareId = approval.SquareId,
                                SquareLabel = squareLabel,
                                NewState = approval.RequestedState,
                                Message = $"Your request to {(approval.RequestedState ? "check" : "uncheck")} '{squareLabel}' has been approved!",
                                Timestamp = DateTime.UtcNow
                            });

                            _logger.LogInformation("Notified client {ConnectionId} (persistent: {PersistentClientId}) about approved request {ApprovalId}", 
                                connectionId, relatedApproval.ClientId, relatedApproval.Id);
                        }
                        else
                        {
                            _logger.LogWarning("Could not find active connection for persistent client {PersistentClientId} to notify about approval {ApprovalId}", 
                                relatedApproval.ClientId, relatedApproval.Id);
                        }
                    }

                    // Notify all admin clients about the approval (using count for clarity)
                    await Clients.Others.SendAsync("ApprovalRequestProcessed", new 
                    { 
                        ApprovalId = approvalId,
                        Status = "Approved",
                        ProcessedBy = adminId,
                        SquareId = approval.SquareId,
                        SquareLabel = squareLabel,
                        RequestedState = approval.RequestedState,
                        RelatedRequestsCount = relatedApprovals.Count,
                        Timestamp = DateTime.UtcNow
                    });

                    // Send global square update to notify all clients (including admins) about the approved change
                    var globalUpdateData = new 
                    { 
                        SquareId = approval.SquareId, 
                        IsChecked = approval.RequestedState,
                        Timestamp = DateTime.UtcNow,
                        Message = $"'{squareLabel}' has been {(approval.RequestedState ? "checked" : "unchecked")} by admin approval"
                    };
                    
                    _logger.LogInformation("Sending GlobalSquareUpdate: SquareId={SquareId}, IsChecked={IsChecked}, Message={Message}", 
                        globalUpdateData.SquareId, globalUpdateData.IsChecked, globalUpdateData.Message);
                    
                    await Clients.Caller.SendAsync("GlobalSquareUpdate", globalUpdateData);

                    _logger.LogInformation("Approved {Count} related requests for square {SquareId} and sent admin update", 
                        relatedApprovals.Count, approval.SquareId);
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", "Failed to approve request");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving request {ApprovalId}", approvalId);
                await Clients.Caller.SendAsync("Error", "Failed to approve request");
            }
        }

        /// <summary>
        /// Admin denies a square marking request
        /// </summary>
        public async Task DenySquareRequest(string approvalId, string? reason = null)
        {
            try
            {
                var adminId = Context.ConnectionId; // In a real app, you'd get this from authentication
                _logger.LogInformation("Admin {AdminId} denying request {ApprovalId}", adminId, approvalId);

                // Get the approval details before processing
                var approval = await _bingoService.GetPendingApprovalAsync(approvalId);
                if (approval == null)
                {
                    await Clients.Caller.SendAsync("Error", "Approval request not found");
                    return;
                }

                // Get all related pending approvals before processing
                var allPendingApprovals = await _bingoService.GetPendingApprovalsAsync();
                var relatedApprovals = allPendingApprovals
                    .Where(a => a.SquareId == approval.SquareId && 
                               a.RequestedState == approval.RequestedState && 
                               a.Status == ApprovalStatus.Pending)
                    .ToList();

                var success = await _bingoService.DenySquareRequestAsync(approvalId, adminId, reason);
                
                if (success)
                {
                    // Get the square details for notification
                    var allSquares = await _bingoService.GetAllSquaresAsync();
                    var square = allSquares.FirstOrDefault(s => s.Id == approval.SquareId);
                    var squareLabel = square?.Label ?? approval.SquareId;

                    // Notify all clients who had related approval requests
                    foreach (var relatedApproval in relatedApprovals)
                    {
                        // Get the current connection ID for this persistent client ID
                        var connectionId = await _clientService.GetConnectionIdFromPersistentClientAsync(relatedApproval.ClientId);
                        
                        if (!string.IsNullOrEmpty(connectionId))
                        {
                            await Clients.Client(connectionId).SendAsync("ApprovalRequestDenied", new 
                            { 
                                ApprovalId = relatedApproval.Id,
                                SquareId = approval.SquareId,
                                SquareLabel = squareLabel,
                                RequestedState = approval.RequestedState,
                                Reason = reason,
                                Message = $"Your request to {(approval.RequestedState ? "check" : "uncheck")} '{squareLabel}' was denied" + 
                                          (string.IsNullOrEmpty(reason) ? "" : $": {reason}"),
                                Timestamp = DateTime.UtcNow
                            });

                            _logger.LogInformation("Notified client {ConnectionId} (persistent: {PersistentClientId}) about denied request {ApprovalId}", 
                                connectionId, relatedApproval.ClientId, relatedApproval.Id);
                        }
                        else
                        {
                            _logger.LogWarning("Could not find active connection for persistent client {PersistentClientId} to notify about denial {ApprovalId}", 
                                relatedApproval.ClientId, relatedApproval.Id);
                        }
                    }

                    // Notify all admin clients about the denial (using count for clarity)
                    await Clients.Others.SendAsync("ApprovalRequestProcessed", new 
                    { 
                        ApprovalId = approvalId,
                        Status = "Denied",
                        ProcessedBy = adminId,
                        SquareId = approval.SquareId,
                        SquareLabel = squareLabel,
                        RequestedState = approval.RequestedState,
                        RelatedRequestsCount = relatedApprovals.Count,
                        Reason = reason,
                        Timestamp = DateTime.UtcNow
                    });

                    _logger.LogInformation("Denied {Count} related requests for square {SquareId}", 
                        relatedApprovals.Count, approval.SquareId);
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", "Failed to deny request");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error denying request {ApprovalId}", approvalId);
                await Clients.Caller.SendAsync("Error", "Failed to deny request");
            }
        }

        /// <summary>
        /// Admin requests list of pending approval requests
        /// </summary>
        public async Task GetPendingApprovals()
        {
            try
            {
                // Clean up expired approvals first
                await _bingoService.CleanupExpiredApprovalsAsync();

                var pendingApprovals = await _bingoService.GetPendingApprovalsAsync();
                await Clients.Caller.SendAsync("PendingApprovalsList", pendingApprovals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving pending approvals");
                await Clients.Caller.SendAsync("Error", "Failed to retrieve pending approvals");
            }
        }

        /// <summary>
        /// Admin broadcasts live mode change to all clients
        /// </summary>
        public async Task BroadcastLiveModeChange(bool isLiveMode)
        {
            try
            {
                var adminId = Context.ConnectionId;
                _logger.LogInformation("Admin {AdminId} changing live mode to {IsLiveMode}", adminId, isLiveMode);

                // Store the live mode state
                await _bingoService.SetLiveModeAsync(isLiveMode);

                // Broadcast the change to all clients
                await Clients.All.SendAsync("LiveModeChanged", new 
                { 
                    IsLiveMode = isLiveMode,
                    Message = isLiveMode ? "Live stream mode activated - approval required for square marking" : "Free play mode activated - mark squares freely!",
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Broadcasted live mode change to all clients: {IsLiveMode}", isLiveMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting live mode change");
                await Clients.Caller.SendAsync("Error", "Failed to change live mode");
            }
        }

        /// <summary>
        /// Get current live mode state
        /// </summary>
        public async Task GetLiveMode()
        {
            try
            {
                var isLiveMode = await _bingoService.GetLiveModeAsync();
                await Clients.Caller.SendAsync("LiveModeReceived", new 
                { 
                    IsLiveMode = isLiveMode,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving live mode");
                await Clients.Caller.SendAsync("Error", "Failed to retrieve live mode");
            }
        }

        /// <summary>
        /// Admin approves all pending square requests at once
        /// </summary>
        public async Task ApproveAllPendingSquares()
        {
            try
            {
                var adminId = Context.ConnectionId;
                _logger.LogInformation("Admin {AdminId} approving all pending requests", adminId);

                // Get all pending approvals before processing
                var pendingApprovals = await _bingoService.GetPendingApprovalsAsync();
                
                if (!pendingApprovals.Any())
                {
                    await Clients.Caller.SendAsync("ApprovalRequestProcessed", new 
                    { 
                        Status = "NoRequests",
                        Message = "No pending approval requests to process",
                        Timestamp = DateTime.UtcNow
                    });
                    return;
                }

                // Group by square and state for notification purposes
                var approvalGroups = pendingApprovals
                    .GroupBy(a => new { a.SquareId, a.RequestedState })
                    .ToList();

                // Approve all pending requests
                var totalProcessed = await _bingoService.ApproveAllPendingRequestsAsync(adminId);

                if (totalProcessed > 0)
                {
                    // Get all squares for labels
                    var allSquares = await _bingoService.GetAllSquaresAsync();

                    // Notify all clients who had approval requests
                    foreach (var group in approvalGroups)
                    {
                        var square = allSquares.FirstOrDefault(s => s.Id == group.Key.SquareId);
                        var squareLabel = square?.Label ?? group.Key.SquareId;

                        // Notify each client in this group
                        foreach (var approval in group)
                        {
                            var connectionId = await _clientService.GetConnectionIdFromPersistentClientAsync(approval.ClientId);
                            
                            if (!string.IsNullOrEmpty(connectionId))
                            {
                                await Clients.Client(connectionId).SendAsync("ApprovalRequestApproved", new 
                                { 
                                    ApprovalId = approval.Id,
                                    SquareId = approval.SquareId,
                                    SquareLabel = squareLabel,
                                    NewState = approval.RequestedState,
                                    Message = $"Your request to {(approval.RequestedState ? "check" : "uncheck")} '{squareLabel}' has been approved!",
                                    Timestamp = DateTime.UtcNow
                                });
                            }
                        }

                        // Send global square update for this group
                        await Clients.All.SendAsync("GlobalSquareUpdate", new 
                        { 
                            SquareId = group.Key.SquareId, 
                            IsChecked = group.Key.RequestedState,
                            Timestamp = DateTime.UtcNow,
                            Message = $"'{squareLabel}' has been {(group.Key.RequestedState ? "checked" : "unchecked")} by admin approval"
                        });
                    }

                    // Notify admin clients about the bulk approval
                    await Clients.All.SendAsync("AllApprovalsProcessed", new 
                    { 
                        Status = "Approved",
                        ProcessedBy = adminId,
                        TotalCount = totalProcessed,
                        GroupCount = approvalGroups.Count,
                        Message = $"Approved {totalProcessed} requests across {approvalGroups.Count} squares",
                        Timestamp = DateTime.UtcNow
                    });

                    _logger.LogInformation("Admin {AdminId} approved {Count} pending requests across {GroupCount} squares", 
                        adminId, totalProcessed, approvalGroups.Count);
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", "Failed to approve pending requests");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving all pending requests");
                await Clients.Caller.SendAsync("Error", "Failed to approve all pending requests");
            }
        }
    }
}
