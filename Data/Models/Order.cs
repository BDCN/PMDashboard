namespace PMDashboard.Data.Models;

public class Order
{
    public int Id { get; set; }

    // ── Core VADATA fields (A–AG) ──
    public string? CustomerPO               { get; set; }
    public string? CustPOLine               { get; set; }
    public string? ShipHold                 { get; set; }
    public string? FaceSite                 { get; set; }
    public string? CustomerOrder            { get; set; }
    public string? OrdLine                  { get; set; }
    public string? AssignSite               { get; set; }
    public string? AssignWhse               { get; set; }
    public string? JobName                  { get; set; }
    public string? IPN                      { get; set; }
    public string? Item                     { get; set; }
    public string? ItemDescription          { get; set; }
    public string? ItemDesignation          { get; set; }
    public double? QtyOrd                   { get; set; }
    public double? QtyShip                  { get; set; }
    public DateTime? ShipDate               { get; set; }
    public DateTime? DeliveryDate           { get; set; }
    public DateTime? WarrantyExpiration     { get; set; }
    public DateTime? EstimatedDeliveryDate  { get; set; }
    public DateTime? EstimatedCompletionDate{ get; set; }
    public DateTime? ActualCompletionDate   { get; set; }
    public DateTime? RequestedCompletion    { get; set; }
    public string? ProductionVariance       { get; set; }
    public string? RequestedOSD             { get; set; }
    public int? DaysLate                    { get; set; }
    public DateTime? PreviousEstCompletionDate { get; set; }
    public DateTime? EstCompletionDateChange{ get; set; }
    public DateTime? ScheduledStartDate     { get; set; }
    public DateTime? ActualStartDate        { get; set; }
    public DateTime? InvoiceDate            { get; set; }
    public string? CustNum                  { get; set; }
    public string? OrderShipTo              { get; set; }
    public string? LineItemShipTo           { get; set; }

    // ── Extended fields — Tableau (Details.xlsx) + Open Lines Report ──
    public string?   CreditHold                    { get; set; } // Tableau C
    public string?   Slsman                        { get; set; } // Tableau G / Open Lines Y: Salesperson
    public string?   OrderStatus                   { get; set; } // Tableau I / Open Lines F
    public string?   ProductCode                   { get; set; } // Tableau Q
    public double?   Price                         { get; set; } // Tableau R / Open Lines M: Line Price
    public double?   ExtPrice                      { get; set; } // Tableau S / Open Lines N: Line Ext
    public int?      SectionCount                  { get; set; } // Tableau T
    public string?   EstCompletionDateChangeReason { get; set; } // Tableau AI
    public DateTime? OrderDate                     { get; set; } // Open Lines D
    public string?   OrderLineStatus               { get; set; } // Open Lines G
    public DateTime? PromiseDate                   { get; set; } // Open Lines H
    public DateTime? LastShipDate                  { get; set; } // Open Lines I: distinct most-recent ship date
    public double?   QtyInvoiced                   { get; set; } // Open Lines O
    public string?   SalesTax1                     { get; set; } // Open Lines S: Sales Tax T
    public string?   SalesTax2                     { get; set; } // Open Lines T: Sales Tax T2
    public double?   TotalCOPrice                  { get; set; } // Open Lines U: total CO price
    public string?   ProjectManagerDataName        { get; set; } // Open Lines V: PM data name
    public string?   CustomerName                  { get; set; } // Open Lines X: customer name

    public DateTime UploadDate   { get; set; }
    public DateTime LastModified { get; set; }

    /// <summary>Email of whoever last wrote this row; stamped by AppDbContext.SaveChanges.</summary>
    public string LastModifiedBy { get; set; } = "";
}
