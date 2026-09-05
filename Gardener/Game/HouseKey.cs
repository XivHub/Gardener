using FFXIVClientStructs.FFXIV.Client.Game;

namespace Gardener.Game;

/// <summary>The current house identity and, when it belongs to the player, which estate slot owns it.</summary>
public readonly record struct HouseKeyInfo(HouseId HouseId, bool Owned, EstateType? OwnedEstateType)
{
    /// <summary>The journal / patch key prefix: <c>HouseId.Id</c> as 16 hex digits.</summary>
    public string KeyString() => HouseId.Id.ToString("x16");
}

/// <summary>Resolves the house the player is currently standing in and confirms ownership.</summary>
public static class HouseKey
{
    /// <summary>Null outside a housing territory or when <c>HousingManager.Instance()</c> is unavailable.</summary>
    public static unsafe HouseKeyInfo? Current()
    {
        // SAFETY: HousingManager.Instance() returns a static client pointer, guaranteed non-null once
        // dereferenced past the null check below. CurrentTerritory is set exactly while the player is
        // inside any housing territory (indoor, outdoor or workshop), which is what "housing
        // territory" means here.
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var houseId = housing->GetCurrentHouseId();
        if (houseId.Id == 0)
            return null;

        EstateType? owned = null;
        if (HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate).Id == houseId.Id)
            owned = EstateType.FreeCompanyEstate;
        else if (HousingManager.GetOwnedHouseId(EstateType.PersonalEstate).Id == houseId.Id)
            owned = EstateType.PersonalEstate;
        else if (HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 0).Id == houseId.Id)
            owned = EstateType.SharedEstate;
        else if (HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 1).Id == houseId.Id)
            owned = EstateType.SharedEstate;

        return new HouseKeyInfo(houseId, owned.HasValue, owned);
    }
}
