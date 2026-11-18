namespace StardewCompanion.Protocol;

public static class PetNaming
{
    public const string Menu="NamingMenu";
    public const string Skill="submit_pet_name";
    public const int MaximumLength=20;

    public static bool IsValidName(string name)=>name.Length is >0 and <=MaximumLength
        &&name==name.Trim()&&!name.Any(char.IsControl);
}
