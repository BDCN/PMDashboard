using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Field-by-field comparison and copy for the Order upsert path, shared by the manual import
/// (OrdersViewModel.UpsertOrders) and the background auto-import (AutoImportService.RunUpsert)
/// so both apply changed values identically instead of drifting apart.
/// </summary>
public static class OrderMerge
{
    public static bool HasChanged(Order a, Order b) =>
        a.CustomerPO != b.CustomerPO || a.CustPOLine != b.CustPOLine ||
        a.ShipHold != b.ShipHold || a.FaceSite != b.FaceSite ||
        a.CustomerOrder != b.CustomerOrder || a.OrdLine != b.OrdLine ||
        a.AssignSite != b.AssignSite || a.AssignWhse != b.AssignWhse ||
        a.JobName != b.JobName || a.IPN != b.IPN ||
        a.Item != b.Item || a.ItemDescription != b.ItemDescription ||
        a.ItemDesignation != b.ItemDesignation ||
        a.QtyOrd != b.QtyOrd || a.QtyShip != b.QtyShip ||
        a.ShipDate != b.ShipDate || a.DeliveryDate != b.DeliveryDate ||
        a.WarrantyExpiration != b.WarrantyExpiration ||
        a.EstimatedDeliveryDate != b.EstimatedDeliveryDate ||
        a.EstimatedCompletionDate != b.EstimatedCompletionDate ||
        a.ActualCompletionDate != b.ActualCompletionDate ||
        a.RequestedCompletion != b.RequestedCompletion ||
        a.ProductionVariance != b.ProductionVariance ||
        a.RequestedOSD != b.RequestedOSD || a.DaysLate != b.DaysLate ||
        a.PreviousEstCompletionDate != b.PreviousEstCompletionDate ||
        a.EstCompletionDateChange != b.EstCompletionDateChange ||
        a.ScheduledStartDate != b.ScheduledStartDate ||
        a.ActualStartDate != b.ActualStartDate || a.InvoiceDate != b.InvoiceDate ||
        a.CustNum != b.CustNum || a.OrderShipTo != b.OrderShipTo ||
        a.LineItemShipTo != b.LineItemShipTo ||
        a.CreditHold != b.CreditHold || a.Slsman != b.Slsman ||
        a.OrderStatus != b.OrderStatus || a.ProductCode != b.ProductCode ||
        a.Price != b.Price || a.ExtPrice != b.ExtPrice ||
        a.SectionCount != b.SectionCount ||
        a.EstCompletionDateChangeReason != b.EstCompletionDateChangeReason ||
        a.OrderDate != b.OrderDate || a.OrderLineStatus != b.OrderLineStatus ||
        a.PromiseDate != b.PromiseDate || a.LastShipDate != b.LastShipDate ||
        a.QtyInvoiced != b.QtyInvoiced || a.SalesTax1 != b.SalesTax1 ||
        a.SalesTax2 != b.SalesTax2 || a.TotalCOPrice != b.TotalCOPrice ||
        a.ProjectManagerDataName != b.ProjectManagerDataName ||
        a.CustomerName != b.CustomerName;

    public static void ApplyChanges(Order t, Order s)
    {
        t.CustomerPO = s.CustomerPO; t.CustPOLine = s.CustPOLine;
        t.ShipHold = s.ShipHold; t.FaceSite = s.FaceSite;
        t.CustomerOrder = s.CustomerOrder; t.OrdLine = s.OrdLine;
        t.AssignSite = s.AssignSite; t.AssignWhse = s.AssignWhse;
        t.JobName = s.JobName; t.IPN = s.IPN;
        t.Item = s.Item; t.ItemDescription = s.ItemDescription;
        t.ItemDesignation = s.ItemDesignation;
        t.QtyOrd = s.QtyOrd; t.QtyShip = s.QtyShip;
        t.ShipDate = s.ShipDate; t.DeliveryDate = s.DeliveryDate;
        t.WarrantyExpiration = s.WarrantyExpiration;
        t.EstimatedDeliveryDate = s.EstimatedDeliveryDate;
        t.EstimatedCompletionDate = s.EstimatedCompletionDate;
        t.ActualCompletionDate = s.ActualCompletionDate;
        t.RequestedCompletion = s.RequestedCompletion;
        t.ProductionVariance = s.ProductionVariance;
        t.RequestedOSD = s.RequestedOSD; t.DaysLate = s.DaysLate;
        t.PreviousEstCompletionDate = s.PreviousEstCompletionDate;
        t.EstCompletionDateChange = s.EstCompletionDateChange;
        t.ScheduledStartDate = s.ScheduledStartDate;
        t.ActualStartDate = s.ActualStartDate; t.InvoiceDate = s.InvoiceDate;
        t.CustNum = s.CustNum; t.OrderShipTo = s.OrderShipTo;
        t.LineItemShipTo = s.LineItemShipTo;
        t.CreditHold = s.CreditHold; t.Slsman = s.Slsman;
        t.OrderStatus = s.OrderStatus; t.ProductCode = s.ProductCode;
        t.Price = s.Price; t.ExtPrice = s.ExtPrice;
        t.SectionCount = s.SectionCount;
        t.EstCompletionDateChangeReason = s.EstCompletionDateChangeReason;
        t.OrderDate = s.OrderDate; t.OrderLineStatus = s.OrderLineStatus;
        t.PromiseDate = s.PromiseDate; t.LastShipDate = s.LastShipDate;
        t.QtyInvoiced = s.QtyInvoiced; t.SalesTax1 = s.SalesTax1;
        t.SalesTax2 = s.SalesTax2; t.TotalCOPrice = s.TotalCOPrice;
        t.ProjectManagerDataName = s.ProjectManagerDataName;
        t.CustomerName = s.CustomerName;
        t.UploadDate = s.UploadDate;
    }
}
