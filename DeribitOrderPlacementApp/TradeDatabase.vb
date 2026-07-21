Imports System.Data.SQLite
Imports System.IO

Public Class TradeDatabase
    Private connectionString As String
    Private dbPath As String

    Public Event DatabaseError(message As String)
    Public Event DatabaseInfo(message As String)   ' item 14a: success/info path (logged green; separate from error red)
    Public Event TradeRecorded(tradeId As Integer, trade As TradeRecord)
    Public Event TradeDeleted(tradeId As Integer) ' Add this new event


    Public Sub New(Optional databasePath As String = "trades.db")
        dbPath = Path.GetFullPath(databasePath)
        connectionString = $"Data Source={dbPath};Version=3;"

        ' Create directory if it doesn't exist
        Dim directoryPath As String = Path.GetDirectoryName(dbPath)
        If Not String.IsNullOrEmpty(directoryPath) AndAlso Not Directory.Exists(directoryPath) Then
            Directory.CreateDirectory(directoryPath)
        End If

        InitializeDatabase()
    End Sub

    Public ReadOnly Property DatabasePath As String
        Get
            Return dbPath
        End Get
    End Property

    Private Sub InitializeDatabase()
        Try
            Using connection As New SQLiteConnection(connectionString)
                connection.Open()

                Dim createTableQuery As String = "
                CREATE TABLE IF NOT EXISTS Trades (
                    TradeId INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME NOT NULL,
                    OrderType TEXT NOT NULL,
                    Direction TEXT NOT NULL,
                    EntryPrice DECIMAL(18,8) NOT NULL,
                    ExitPrice DECIMAL(18,8) NOT NULL,
                    OrderSizeUSD DECIMAL(18,8) NOT NULL,
                    ProfitLossUSD DECIMAL(18,8) NOT NULL,
                    IsProfit BOOLEAN NOT NULL
                );"

                Using command As New SQLiteCommand(createTableQuery, connection)
                    command.ExecuteNonQuery()
                End Using

                ' Ergonomics item C: additive schema migration for the trade-quality columns.
                ' Idempotent: SQLite throws "duplicate column name" on an existing column - those
                ' are swallowed per column; anything else surfaces via DatabaseError as usual.
                MigrateSchema(connection)

                ' Create indexes for better performance
                CreateIndexes(connection)
            End Using

        Catch ex As Exception
            RaiseEvent DatabaseError($"Failed to initialize database: {ex.Message}")
            Throw
        End Try
    End Sub

    ' Item C migration: one ALTER per column, individually try/caught. Existing rows read the
    ' DEFAULTs (0 / '') through CreateTradeFromReader's null-safe converters either way.
    Private Sub MigrateSchema(connection As SQLiteConnection)
        Dim newColumns() As String = {
            "ALTER TABLE Trades ADD COLUMN MaeUSD DECIMAL(18,8) NOT NULL DEFAULT 0;",
            "ALTER TABLE Trades ADD COLUMN MfeUSD DECIMAL(18,8) NOT NULL DEFAULT 0;",
            "ALTER TABLE Trades ADD COLUMN PlannedStop DECIMAL(18,8) NOT NULL DEFAULT 0;",
            "ALTER TABLE Trades ADD COLUMN RMultiple DECIMAL(18,8) NOT NULL DEFAULT 0;",
            "ALTER TABLE Trades ADD COLUMN FeesUSD DECIMAL(18,8) NOT NULL DEFAULT 0;",
            "ALTER TABLE Trades ADD COLUMN SignalId TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE Trades ADD COLUMN SignalConfidence TEXT NOT NULL DEFAULT '';"
        }
        For Each alterQuery In newColumns
            Try
                Using command As New SQLiteCommand(alterQuery, connection)
                    command.ExecuteNonQuery()
                End Using
            Catch ex As SQLiteException When ex.Message.Contains("duplicate column name")
                ' Column already exists - the migration has run before. Expected on every start.
            End Try
        Next
    End Sub

    Private Sub CreateIndexes(connection As SQLiteConnection)
        Dim indexQueries() As String = {
            "CREATE INDEX IF NOT EXISTS idx_timestamp ON Trades(Timestamp);",
            "CREATE INDEX IF NOT EXISTS idx_direction ON Trades(Direction);",
            "CREATE INDEX IF NOT EXISTS idx_order_type ON Trades(OrderType);",
            "CREATE INDEX IF NOT EXISTS idx_profit ON Trades(IsProfit);"
        }

        For Each indexQuery In indexQueries
            Using command As New SQLiteCommand(indexQuery, connection)
                command.ExecuteNonQuery()
            End Using
        Next
    End Sub

    Public Function RecordCompletedTrade(trade As TradeRecord) As Integer
        Try
            Using connection As New SQLiteConnection(connectionString)
                connection.Open()

                Dim insertQuery As String = "
                INSERT INTO Trades (
                    Timestamp, OrderType, Direction, EntryPrice, ExitPrice,
                    OrderSizeUSD, ProfitLossUSD, IsProfit,
                    MaeUSD, MfeUSD, PlannedStop, RMultiple, FeesUSD, SignalId, SignalConfidence
                ) VALUES (
                    @Timestamp, @OrderType, @Direction, @EntryPrice, @ExitPrice,
                    @OrderSizeUSD, @ProfitLossUSD, @IsProfit,
                    @MaeUSD, @MfeUSD, @PlannedStop, @RMultiple, @FeesUSD, @SignalId, @SignalConfidence
                )"

                Using command As New SQLiteCommand(insertQuery, connection)
                    command.Parameters.AddWithValue("@Timestamp", trade.Timestamp)
                    command.Parameters.AddWithValue("@OrderType", trade.OrderType)
                    command.Parameters.AddWithValue("@Direction", trade.Direction)
                    command.Parameters.AddWithValue("@EntryPrice", trade.EntryPrice)
                    command.Parameters.AddWithValue("@ExitPrice", trade.ExitPrice)
                    command.Parameters.AddWithValue("@OrderSizeUSD", trade.OrderSizeUSD)
                    command.Parameters.AddWithValue("@ProfitLossUSD", trade.ProfitLossUSD)
                    command.Parameters.AddWithValue("@IsProfit", trade.IsProfit)
                    command.Parameters.AddWithValue("@MaeUSD", trade.MaeUSD)
                    command.Parameters.AddWithValue("@MfeUSD", trade.MfeUSD)
                    command.Parameters.AddWithValue("@PlannedStop", trade.PlannedStop)
                    command.Parameters.AddWithValue("@RMultiple", trade.RMultiple)
                    command.Parameters.AddWithValue("@FeesUSD", trade.FeesUSD)
                    command.Parameters.AddWithValue("@SignalId", If(trade.SignalId, ""))
                    command.Parameters.AddWithValue("@SignalConfidence", If(trade.SignalConfidence, ""))

                    command.ExecuteNonQuery()

                    ' Get the inserted trade ID
                    command.CommandText = "SELECT last_insert_rowid();"
                    Dim tradeId = Convert.ToInt32(command.ExecuteScalar())

                    RaiseEvent TradeRecorded(tradeId, trade)
                    Return tradeId
                End Using
            End Using

        Catch ex As Exception
            RaiseEvent DatabaseError($"Failed to record completed trade: {ex.Message}")
            Throw
        End Try
    End Function

    Public Function GetAllTrades() As List(Of TradeRecord)
        Dim trades As New List(Of TradeRecord)

        Try
            Using connection As New SQLiteConnection(connectionString)
                connection.Open()

                Dim query As String = "SELECT * FROM Trades ORDER BY Timestamp DESC"

                Using command As New SQLiteCommand(query, connection)
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            trades.Add(CreateTradeFromReader(reader))
                        End While
                    End Using
                End Using
            End Using
        Catch ex As Exception
            RaiseEvent DatabaseError($"Error getting trades: {ex.Message}")
        End Try

        Return trades
    End Function

    Private Function CreateTradeFromReader(reader As SQLiteDataReader) As TradeRecord
        Return New TradeRecord With {
            .TradeId = Convert.ToInt32(reader("TradeId")),
            .Timestamp = Convert.ToDateTime(reader("Timestamp")),
            .OrderType = reader("OrderType").ToString(),
            .Direction = reader("Direction").ToString(),
            .EntryPrice = Convert.ToDecimal(reader("EntryPrice")),
            .ExitPrice = Convert.ToDecimal(reader("ExitPrice")),
            .OrderSizeUSD = Convert.ToDecimal(reader("OrderSizeUSD")),
            .ProfitLossUSD = Convert.ToDecimal(reader("ProfitLossUSD")),
            .IsProfit = Convert.ToBoolean(reader("IsProfit")),
            .MaeUSD = ReadDecimalOrZero(reader, "MaeUSD"),
            .MfeUSD = ReadDecimalOrZero(reader, "MfeUSD"),
            .PlannedStop = ReadDecimalOrZero(reader, "PlannedStop"),
            .RMultiple = ReadDecimalOrZero(reader, "RMultiple"),
            .FeesUSD = ReadDecimalOrZero(reader, "FeesUSD"),
            .SignalId = ReadStringOrEmpty(reader, "SignalId"),
            .SignalConfidence = ReadStringOrEmpty(reader, "SignalConfidence")
        }
    End Function

    ' Item C: null-safe readers for the migrated columns (pre-migration rows are NULL there;
    ' Convert.ToDecimal(DBNull) would throw and drop the whole trade list).
    Private Shared Function ReadDecimalOrZero(reader As SQLiteDataReader, column As String) As Decimal
        Dim value As Object = reader(column)
        Return If(value Is Nothing OrElse value Is DBNull.Value, 0D, Convert.ToDecimal(value))
    End Function

    Private Shared Function ReadStringOrEmpty(reader As SQLiteDataReader, column As String) As String
        Dim value As Object = reader(column)
        Return If(value Is Nothing OrElse value Is DBNull.Value, "", value.ToString())
    End Function

    Public Function DeleteTrade(tradeId As Integer) As Boolean
        Try
            Using connection As New SQLiteConnection(connectionString)
                connection.Open()

                Dim deleteQuery As String = "DELETE FROM Trades WHERE TradeId = @TradeId"

                Using command As New SQLiteCommand(deleteQuery, connection)
                    command.Parameters.AddWithValue("@TradeId", tradeId)

                    Dim rowsAffected = command.ExecuteNonQuery()

                    If rowsAffected > 0 Then
                        RaiseEvent TradeDeleted(tradeId)
                        Return True
                    Else
                        RaiseEvent DatabaseError($"Trade with ID {tradeId} not found")
                        Return False
                    End If
                End Using
            End Using

        Catch ex As Exception
            RaiseEvent DatabaseError($"Failed to delete trade: {ex.Message}")
            Return False
        End Try
    End Function

    Public Function DeleteMultipleTrades(tradeIds As List(Of Integer)) As Integer
        Try
            Using connection As New SQLiteConnection(connectionString)
                connection.Open()

                Dim deletedCount As Integer = 0

                Using transaction = connection.BeginTransaction()
                    Try
                        For Each tradeId In tradeIds
                            Dim deleteQuery As String = "DELETE FROM Trades WHERE TradeId = @TradeId"

                            Using command As New SQLiteCommand(deleteQuery, connection, transaction)
                                command.Parameters.AddWithValue("@TradeId", tradeId)

                                If command.ExecuteNonQuery() > 0 Then
                                    deletedCount += 1
                                End If
                            End Using
                        Next

                        transaction.Commit()
                        RaiseEvent DatabaseInfo($"Successfully deleted {deletedCount} trades")
                        Return deletedCount

                    Catch ex As Exception
                        transaction.Rollback()
                        Throw
                    End Try
                End Using
            End Using

        Catch ex As Exception
            RaiseEvent DatabaseError($"Failed to delete multiple trades: {ex.Message}")
            Return 0
        End Try
    End Function

End Class
