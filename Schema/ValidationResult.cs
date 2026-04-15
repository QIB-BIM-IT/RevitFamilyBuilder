using System.Collections.Generic;

namespace RevitFamilyBuilder.Schema
{
    public class ValidationResult
    {
        public bool IsValid { get; private set; }
        public List<string> Errors { get; private set; }

        private ValidationResult(bool isValid, List<string> errors)
        {
            IsValid = isValid;
            Errors = errors;
        }

        public static ValidationResult Ok()
        {
            return new ValidationResult(true, new List<string>());
        }

        public static ValidationResult Fail(params string[] errors)
        {
            return new ValidationResult(false, new List<string>(errors));
        }
    }
}
