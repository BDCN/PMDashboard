using ClosedXML.Excel;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public class ExcelImportService
{
    // Column index constants (1-based, matching ClosedXML)
    private const int ColCustomerPO = 1;       // A
    private const int ColCustPOLine = 2;        // B
    private const int ColShipHold = 3;          // C
    private const int ColFaceSite = 4;          // D
    private const int ColCustomerOrder = 5;     // E
    private const int ColOrdLine = 6;           // F
    private const int ColAssignSite = 7;        // G
    private const int ColAssignWhse = 8;        // H
    private const int ColJobName = 9;           // I
    private const int ColIPN = 10;              // J
    private const int ColItem = 11;             // K
    private const int ColItemDescription = 12;  // L
    private const int ColItemDesignation = 13;  // M
    private const int ColQtyOrd = 14;           // N
    private const int ColQtyShip = 15;          // O
    private const int ColShipDate = 16;         // P
    private const int ColDeliveryDate = 17;     // Q
    private const int ColWarrantyExpiration = 18; // R
    private const int ColEstDeliveryDate = 19;  // S
    private const int ColEstCompletionDate = 20; // T
    private const int ColActualCompletionDate = 21; // U
    private const int ColRequestedCompletion = 22; // V
    private const int ColProductionVariance = 23; // W
    private const int ColRequestedOSD = 24;     // X
    private const int ColDaysLate = 25;         // Y
    private const int ColPrevEstCompletion = 26; // Z
    private const int ColEstCompletionChange = 27; // AA
    private const int ColScheduledStart = 28;   // AB
    private const int ColActualStart = 29;      // AC
    private const int ColInvoiceDate = 30;      // AD
    private const int ColCustNum = 31;          // AE
    private const int ColOrderShipTo = 32;      // AF
    private const int ColLineItemShipTo = 33;   // AG

    public List<Order> ImportOrders(string filePath)
    {
        var orders = new List<Order>();

        using var workbook = new XLWorkbook(filePath);
        var sheet = workbook.Worksheets.First();

        // Skip header row (row 1), start from row 2
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            var r = sheet.Row(row);

            // Skip fully empty rows
            if (string.IsNullOrWhiteSpace(GetString(r, ColCustomerPO)) &&
                string.IsNullOrWhiteSpace(GetString(r, ColJobName)) &&
                string.IsNullOrWhiteSpace(GetString(r, ColItem)))
                continue;

            orders.Add(new Order
            {
                CustomerPO = GetString(r, ColCustomerPO),
                CustPOLine = GetString(r, ColCustPOLine),
                ShipHold = GetString(r, ColShipHold),
                FaceSite = GetString(r, ColFaceSite),
                CustomerOrder = GetString(r, ColCustomerOrder),
                OrdLine = GetString(r, ColOrdLine),
                AssignSite = GetString(r, ColAssignSite),
                AssignWhse = GetString(r, ColAssignWhse),
                JobName = GetString(r, ColJobName),
                IPN = GetString(r, ColIPN),
                Item = GetString(r, ColItem),
                ItemDescription = GetString(r, ColItemDescription),
                ItemDesignation = GetString(r, ColItemDesignation),
                QtyOrd = GetDouble(r, ColQtyOrd),
                QtyShip = GetDouble(r, ColQtyShip),
                ShipDate = GetDate(r, ColShipDate),
                DeliveryDate = GetDate(r, ColDeliveryDate),
                WarrantyExpiration = GetDate(r, ColWarrantyExpiration),
                EstimatedDeliveryDate = GetDate(r, ColEstDeliveryDate),
                EstimatedCompletionDate = GetDate(r, ColEstCompletionDate),
                ActualCompletionDate = GetDate(r, ColActualCompletionDate),
                RequestedCompletion = GetDate(r, ColRequestedCompletion),
                ProductionVariance = GetString(r, ColProductionVariance),
                RequestedOSD = GetString(r, ColRequestedOSD),
                DaysLate = GetInt(r, ColDaysLate),
                PreviousEstCompletionDate = GetDate(r, ColPrevEstCompletion),
                EstCompletionDateChange = GetDate(r, ColEstCompletionChange),
                ScheduledStartDate = GetDate(r, ColScheduledStart),
                ActualStartDate = GetDate(r, ColActualStart),
                InvoiceDate = GetDate(r, ColInvoiceDate),
                CustNum = GetString(r, ColCustNum),
                OrderShipTo = GetString(r, ColOrderShipTo),
                LineItemShipTo = GetString(r, ColLineItemShipTo),
            });
        }

        return orders;
    }

    // ── Tableau (Details.xlsx) parser — 42 columns ──
    public List<Order> ImportTableauOrders(string filePath)
    {
        var orders = new List<Order>();
        using var workbook = new XLWorkbook(filePath);
        var sheet   = workbook.Worksheets.First();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            var r = sheet.Row(row);
            if (string.IsNullOrWhiteSpace(GetString(r, 1)) && string.IsNullOrWhiteSpace(GetString(r, 12)))
                continue;

            orders.Add(new Order
            {
                CustomerPO    = GetString(r, 1),   // A: Customer Po
                CustPOLine    = GetString(r, 2),   // B: Customer Po Line
                CreditHold    = GetString(r, 3),   // C: Credit Hold      ← new
                ShipHold      = GetString(r, 4),   // D: Ship Hold
                FaceSite      = GetString(r, 5),   // E: Face Site
                CustomerOrder = GetString(r, 6),   // F: Customer Order
                Slsman        = GetString(r, 7),   // G: Slsman            ← new
                OrdLine       = GetString(r, 8),   // H: Order Line
                OrderStatus   = GetString(r, 9),   // I: Status            ← new
                AssignSite    = GetString(r, 10),  // J: Assign Site
                AssignWhse    = GetString(r, 11),  // K: Assign Warehouse
                JobName       = GetString(r, 12),  // L: Job Name
                IPN           = GetString(r, 13),  // M: IPN
                Item          = GetString(r, 14),  // N: Item
                ItemDescription  = GetString(r, 15), // O
                ItemDesignation  = GetString(r, 16), // P
                ProductCode   = GetString(r, 17),  // Q: Product Code      ← new
                Price         = GetDouble(r, 18),  // R: Price             ← new
                ExtPrice      = GetDouble(r, 19),  // S: Ext Price         ← new
                SectionCount  = GetInt(r, 20),     // T: Section Count     ← new
                QtyOrd        = GetDouble(r, 21),  // U: Qty Ordered
                QtyShip       = GetDouble(r, 22),  // V: Qty Shipped
                ShipDate      = GetDate(r, 23),    // W
                DeliveryDate  = GetDate(r, 24),    // X
                WarrantyExpiration       = GetDate(r, 25),
                EstimatedDeliveryDate    = GetDate(r, 26),
                EstimatedCompletionDate  = GetDate(r, 27),
                ActualCompletionDate     = GetDate(r, 28),
                RequestedCompletion      = GetDate(r, 29),
                ProductionVariance       = GetString(r, 30),
                RequestedOSD             = GetString(r, 31),
                DaysLate                 = GetInt(r, 32),
                PreviousEstCompletionDate = GetDate(r, 33),
                EstCompletionDateChange  = GetDate(r, 34),
                EstCompletionDateChangeReason = GetString(r, 35), // AI ← new
                ScheduledStartDate       = GetDate(r, 36),
                ActualStartDate          = GetDate(r, 37),
                InvoiceDate              = GetDate(r, 38),
                CustNum                  = GetString(r, 39),
                OrderShipTo              = GetString(r, 40),
                LineItemShipTo           = GetString(r, 41),
                // Col 42 (AP): Fivetran sync timestamp — skipped
            });
        }
        return orders;
    }

    // ── Open Lines Report parser — 25 columns ──
    public List<Order> ImportOpenLinesOrders(string filePath)
    {
        var orders = new List<Order>();
        using var workbook = new XLWorkbook(filePath);
        var sheet   = workbook.Worksheets.First();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            var r = sheet.Row(row);
            if (string.IsNullOrWhiteSpace(GetString(r, 3)) && string.IsNullOrWhiteSpace(GetString(r, 17)))
                continue;

            orders.Add(new Order
            {
                FaceSite      = GetString(r, 1),   // A: Site → FaceSite
                AssignSite    = GetString(r, 2),   // B: Assign Site
                CustomerOrder = GetString(r, 3),   // C: CO
                OrderDate     = GetDate(r, 4),     // D: Order Date        ← new
                OrdLine       = GetString(r, 5),   // E: Line
                OrderStatus   = GetString(r, 6),   // F: Order Status
                OrderLineStatus = GetString(r, 7), // G: Order Line Status ← new
                PromiseDate   = GetDate(r, 8),     // H: Promise Date      ← new
                LastShipDate  = GetDate(r, 9),     // I: Last Ship Date    ← new (distinct)
                Item          = GetString(r, 10),  // J
                ItemDescription = GetString(r, 11),// K: Description
                QtyOrd        = GetDouble(r, 12),  // L: Qty Ordered
                Price         = GetDouble(r, 13),  // M: Line Price → Price
                ExtPrice      = GetDouble(r, 14),  // N: Line Ext → ExtPrice
                QtyInvoiced   = GetDouble(r, 15),  // O: Qty Invoiced      ← new
                QtyShip       = GetDouble(r, 16),  // P: Qty Shipped
                JobName       = GetString(r, 17),  // Q: Job → JobName
                // R (18): Price — same field, skip duplicate
                SalesTax1     = GetString(r, 19),  // S: Sales Tax T       ← new
                SalesTax2     = GetString(r, 20),  // T: Sales Tax T2      ← new
                TotalCOPrice  = GetDouble(r, 21),  // U: Price 2 (total CO)← new
                ProjectManagerDataName = GetString(r, 22), // V            ← new
                CustNum       = GetString(r, 23),  // W: Cust Num
                CustomerName  = GetString(r, 24),  // X: Name              ← new
                Slsman        = GetString(r, 25),  // Y: Salesperson → Slsman
            });
        }
        return orders;
    }

    private static string? GetString(IXLRow row, int col)
    {
        var val = row.Cell(col).GetString();
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static double? GetDouble(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return cell.GetDouble();
        if (double.TryParse(cell.GetString(), out var d)) return d;
        return null;
    }

    private static int? GetInt(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        if (int.TryParse(cell.GetString(), out var i)) return i;
        return null;
    }

    private static DateTime? GetDate(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime();
        var str = cell.GetString();
        if (DateTime.TryParse(str, out var dt)) return dt;
        return null;
    }
}
