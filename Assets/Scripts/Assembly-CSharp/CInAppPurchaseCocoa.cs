
using System.Collections.Generic;
using UnityEngine;

// Offline iOS implementation.
// In-app purchases are intentionally unavailable.

public class CInAppPurchaseCocoa : ICInAppPurchase
{
    public override void Init(
        string[] products,
        string marketPublicKey)
    {
        Debug.Log("Offline IAP: initialization skipped.");
    }

    public override void Init(
        Dictionary<string, string> products,
        string marketPublicKey)
    {
        Debug.Log("Offline IAP: initialization skipped.");
    }

    public override CInAppPurchaseProduct[] GetAvailableProducts()
    {
        return null;
    }

    public override void BuyProduct(string product)
    {
        Debug.LogWarning(
            "Offline IAP: purchases are unavailable."
        );
    }

    public override bool IsTurnedOn()
    {
        return false;
    }

    public override bool IsAvailable()
    {
        return false;
    }

    public override string RetrieveProduct()
    {
        return null;
    }

    public override TRANSACTION_STATE GetPurchaseTransactionStatus()
    {
        return TRANSACTION_STATE.NONE;
    }

    public override void RestoreCompletedTransactions()
    {
        Debug.Log(
            "Offline IAP: restore unavailable."
        );
    }

    public override RESTORE_STATE GetRestoreStatus()
    {
        return RESTORE_STATE.NONE;
    }

    public override int GetRetrievalQueueCount()
    {
        return 0;
    }

    public override string GetRetrievalQueueItem(int index)
    {
        return null;
    }

    public override void RetrievalQueueDispose(int numItems)
    {
    }
}
