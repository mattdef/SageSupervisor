using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace SageSupervisor.Models;

public class ServiceBrokerMonitor(string connectionString) : IDisposable
{
    private readonly string _connectionString = connectionString;
    private SqlConnection? _connection;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private TableChangeEventArgs? checkDoubleValueMemory;
    
    public event EventHandler<TableChangeEventArgs>? TableChanged;

    public void Start()
    {
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            return;

        _cancellationTokenSource = new CancellationTokenSource();
        _monitoringTask = Task.Run(() => MonitorChangesAsync(_cancellationTokenSource.Token));
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();
        
        try
        {
            _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ignorer les exceptions liées à l'annulation
        }
        
        _connection?.Close();
    }

    private async Task MonitorChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using (_connection = new SqlConnection(_connectionString))
            {
                await _connection.OpenAsync(cancellationToken);
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Commande modifiée pour séparer la réception du message de l'affectation de variable
                    using SqlCommand command = _connection.CreateCommand();
                    command.CommandText = @"
                            DECLARE @ConversationGroupId uniqueidentifier;
                            DECLARE @MessageTypeName nvarchar(256);
                            DECLARE @MessageBody varbinary(max);
                            
                            WAITFOR (
                                RECEIVE TOP(1)
                                    @ConversationGroupId = conversation_group_id,
                                    @MessageTypeName = message_type_name,
                                    @MessageBody = message_body
                                FROM TableModificationQueue
                            ), TIMEOUT 30000;
                            
                            -- Renvoyer les résultats séparément
                            IF (@MessageTypeName IS NOT NULL)
                            BEGIN
                                SELECT @MessageTypeName AS MessageType, 
                                    CAST(@MessageBody AS nvarchar(max)) AS MessageBody;
                            END
                            ELSE
                            BEGIN
                                SELECT 'TIMEOUT' AS MessageType, NULL AS MessageBody;
                            END
                        ";

                    command.CommandTimeout = 60; // Secondes

                    using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        string messageType = reader.GetString(0);

                        // Si c'est juste un timeout, continuer la boucle
                        if (messageType == "TIMEOUT")
                            continue;

                        // Si c'est un vrai message, traiter les données
                        if (!reader.IsDBNull(1))
                        {
                            string messageBody = reader.GetString(1);
                            ProcessMessage(messageBody);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Surveillance annulée normalement
        }
        catch (Exception ex)
        {
            // Gérer les autres exceptions
            Console.WriteLine($"Erreur lors de la surveillance: {ex.Message}");
            
            // Redémarrer la surveillance après un délai en cas d'erreur
            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(5000, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    _monitoringTask = Task.Run(() => MonitorChangesAsync(cancellationToken));
            }
        }
    }

    private void ProcessMessage(string messageBody)
    {
        try
        {
            XDocument xmlDoc = XDocument.Parse(messageBody);
            if (xmlDoc.Root is null)
                return;

            XElement rootElement = xmlDoc.Root;
            
            if (rootElement.Name == "F_DOCENTETE")
            {
                foreach (XElement modificationElement in rootElement.Elements())
                {
                    string operationType = modificationElement.Attribute("OperationType")!.Value;
                    string recordID = modificationElement.Attribute("RecordID")!.Value;
                    string modificationTime = modificationElement.Attribute("ModificationTime")!.Value;
                    int domaine = int.Parse(modificationElement.Attribute("Domaine")!.Value);
                    int type = int.Parse(modificationElement.Attribute("Type")!.Value);
                    
                    if (!string.IsNullOrEmpty(operationType) 
                        && !string.IsNullOrEmpty(recordID)
                        && domaine == 0 
                        && type == 0)
                    {
                        DateTime timeStamp = DateTime.Parse(modificationTime);
                        
                        // Convertir le type d'opération en enum
                        TableChangeType changeType = operationType switch
                        {
                            "INSERT" => TableChangeType.Insert,
                            "UPDATE" => TableChangeType.Update,
                            "DELETE" => TableChangeType.Delete,
                            _ => TableChangeType.Unknown
                        };

                        // Test doublon
                        if (checkDoubleValueMemory is not null)
                        {
                            if (checkDoubleValueMemory.RecordId == recordID
                            && checkDoubleValueMemory.ChangeType == changeType
                            && checkDoubleValueMemory.Timestamp > timeStamp.AddSeconds(-5))
                            continue;
                        }
                        checkDoubleValueMemory = new TableChangeEventArgs(recordID, changeType, timeStamp, domaine, type);
                        
                        // Déclencher l'événement
                        TableChanged?.Invoke(this, new TableChangeEventArgs(recordID, changeType, timeStamp, domaine, type));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur lors du traitement du message: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _cancellationTokenSource?.Dispose();
        _connection?.Dispose();
    }
}