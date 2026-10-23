using TransparentValueObjects;
namespace SilverModManager.Abstractions.GOG.Values;

[ValueObject<ulong>]
public  readonly partial struct ProductId : IAugmentWith<JsonAugment>
{
    
}
