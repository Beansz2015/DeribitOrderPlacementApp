Imports System.Data.SQLite
Imports System.IO
Public Class TradeRecord
    Public Property TradeId As Integer
    Public Property Timestamp As DateTime ' Exit timestamp
    Public Property OrderType As String ' Limit, Trailing, Market
    Public Property Direction As String ' Long, Short
    Public Property EntryPrice As Decimal
    Public Property ExitPrice As Decimal
    Public Property OrderSizeUSD As Decimal
    Public Property ProfitLossUSD As Decimal
    Public Property IsProfit As Boolean

    ' Entry slippage (docs/spec-trade-slippage-fields.md §2.2), snapshotted at the flat->nonzero
    ' transition. SignalPrice is NOT the engine's signal price, despite the name (kept per owner
    ' ruling R1): it is the chase ANCHOR - the own-side price at the first ATR-slippage check of
    ' this entry attempt (the placement price of a limit entry with ATRSlip ticked). 0 = the entry
    ' never armed the guard (market entry, or ATRSlip unticked), and then SlippageATR is 0 too.
    ' SlippageATR = |average entry - anchor| / the effective ATR captured WITH the anchor.
    ' RequoteCount = chase edits sent for this entry. AttemptType/MaxSlippageExceeded were deleted:
    ' on a completed trade they were constant; an abort is an AbortedEntryRecord (ruling R2).
    Public Property RequoteCount As Integer = 0
    Public Property SignalPrice As Decimal = 0
    Public Property SlippageATR As Decimal = 0

    ' Ergonomics item C (docs/spec-execution-ergonomics.md): trade-quality metrics for
    ' structural-stop calibration. MAE <= 0 <= MFE (sign-adjusted USD excursions vs entry);
    ' RMultiple = signed P/L over the PLANNED risk (0 when the planned stop was unknown);
    ' FeesUSD = cumulative BTC fees x index at close. SignalId/SignalConfidence are written
    ' empty in Phase A and populated by the bridge consumer in Phase B.
    Public Property MaeUSD As Decimal = 0
    Public Property MfeUSD As Decimal = 0
    Public Property PlannedStop As Decimal = 0
    Public Property RMultiple As Decimal = 0
    Public Property FeesUSD As Decimal = 0
    Public Property SignalId As String = ""
    Public Property SignalConfidence As String = ""

    Public Sub New()
        Timestamp = DateTime.UtcNow
    End Sub

    Public Sub New(orderType As String, direction As String, entryPrice As Decimal,
                   exitPrice As Decimal, orderSizeUSD As Decimal, profitLossUSD As Decimal, isProfit As Boolean)
        Me.New() ' Call default constructor
        Me.OrderType = orderType
        Me.Direction = direction
        Me.EntryPrice = entryPrice
        Me.ExitPrice = exitPrice
        Me.OrderSizeUSD = orderSizeUSD
        Me.ProfitLossUSD = profitLossUSD
        Me.IsProfit = isProfit
    End Sub
End Class

' One aborted entry attempt (docs/spec-trade-slippage-fields.md §2.3, owner ruling R2). Lives in
' its own AbortedEntries table, so an abort never touches Trades or any P/L, win-rate or R query.
Public Class AbortedEntryRecord
    Public Property AbortId As Integer
    Public Property Timestamp As DateTime ' UTC
    Public Property Direction As String ' Long, Short
    Public Property Reason As String ' "ATR slippage" | "EV floor" | an early-return path name
    Public Property Anchor As Decimal ' the chase anchor at abort (see TradeRecord.SignalPrice)
    Public Property LastQuote As Decimal ' the own-side quote that tripped it
    Public Property SlippageATR As Decimal ' |LastQuote - Anchor| / AtrUsed
    Public Property AtrUsed As Decimal ' the effective ATR in force at the abort
    Public Property AtrSource As String = ""
    Public Property RequoteCount As Integer
    Public Property SignalId As String = "" ' the staged bridge signal id, "" for a manual entry
End Class

