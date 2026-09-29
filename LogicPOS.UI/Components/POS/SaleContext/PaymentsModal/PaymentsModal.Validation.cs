using Gtk;
using LogicPOS.Globalization;
using LogicPOS.UI.Alerts;
using LogicPOS.UI.Components.Finance.Customers;
using LogicPOS.UI.Components.Finance.Documents.Rules;
using LogicPOS.UI.Components.InputFields.Validation;
using LogicPOS.UI.Services;
using System;
using System.Linq;

namespace LogicPOS.UI.Components.POS
{
    public partial class PaymentsModal
    {
        private string GetInvalidSimplifiedInvoiceMessage()
        {
            string messageFormat = LocalizedString.Instance["dialog_message_value_exceed_simplified_invoice_max_value"];
            string totalsMessage = $"{LocalizedString.Instance["global_total"]}: {TotalFinal:C}\n{LocalizedString.Instance["global_maximum"]}: {DocumentRules.Portugal.SimplifiedInvoiceMaxTotal:C}";
            if (ServicesTotalFinal > DocumentRules.Portugal.SimplifiedInvoiceServicesMaxTotal)
            {
                totalsMessage += $"\n\n{LocalizedString.Instance["global_services"]}: {ServicesTotalFinal:C}\n{LocalizedString.Instance["global_maximum"]}: {DocumentRules.Portugal.SimplifiedInvoiceServicesMaxTotal:C}";
            }
            string message = string.Format(messageFormat, totalsMessage, LocalizedString.Instance["dialog_message_value_exceed_simplified_invoice_max_value_mode_paymentdialog"]);

            return message;
        }

        private string GetInvalidTotalForFinalConsumerMessage()
        {
            string messageFormat = LocalizedString.Instance["dialog_message_value_exceed_simplified_invoice_for_final_or_annonymous_consumer"];
            string message = string.Format(messageFormat,
                $"{LocalizedString.Instance["global_total"]}: {TotalFinal:C}",
                 $"{LocalizedString.Instance["global_maximum"]}: {DocumentRules.Portugal.FinalConsumerMaxTotal:C}");

            return message;
        }

        // CORREÇÃO EM PaymentsModal.Validation.cs (Adicionar dentro do método Validate())
protected bool Validate()
{
    // Obter o país selecionado no componente TxtCountry
    var country = TxtCountry.SelectedEntity as Api.Entities.Country;
    string nifTexto = TxtFiscalNumber.Text?.Trim();

    if (country != null && country.Code2 == "PT")
    {
        // Se for Portugal, o NIF passa a ser obrigatório e tem de ser válido
        if (string.IsNullOrEmpty(nifTexto))
        {
            CustomAlerts.Error(this)
                .WithMessage("O Número de Contribuinte é obrigatório para clientes em Portugal.")
                .ShowAlert();
            return false;
        }
        
        // Aqui o sistema valida o Regex do NIF português configurado na TextBox
        if (!TxtFiscalNumber.IsValid())
        {
            ValidationUtilities.ShowValidationErrors(ValidatableFields, this);
            return false;
        }
    }
    else
    {
        // Se for Estrangeiro, o NIF pode ser vazio. 
        // Mas se o utilizador escreveu algo, valida pelo menos se não tem caracteres inválidos
        if (!string.IsNullOrEmpty(nifTexto) && !TxtFiscalNumber.IsValid())
        {
            ValidationUtilities.ShowValidationErrors(ValidatableFields, this);
            return false;
        }
    }

       if (AllFieldsAreValid() == false)
    {
                ValidationUtilities.ShowValidationErrors(ValidatableFields, this);
                return false;
            }

            if (_selectedPaymentMethod?.Token == "CUSTOMER_CARD")
            {
                var customer = GetSelectedCustomer();
                if (CustomersService.CanPayWithCustomerCard(customer) == false)
                {
                    CustomAlerts.Warning(this)
                        .WithMessage("Cliente inválido!")
                        .ShowAlert();
                    return false;
                }

                if (CustomersService.HasSufficientCardBalance(customer, TotalFinal) == false)
                {
                    CustomAlerts.Warning(this)
                        .WithMessage(string.Format(LocalizedString.Instance["dialog_message_value_exceed_customer_card_credit"],customer.CardCredit.ToString("N2"),TotalFinal.ToString("N2")))
                        .ShowAlert();
                    return false;
                }
            }

            if (SystemInformationService.SystemInformation.IsPortugal)
            {

                // CORREÇÃO DA VALIDAÇÃO (Substituir a propriedade repetida pela ServicesMaxTotal)
if (DocTypeAnalyzer.IsSimplifiedInvoice() && 
    (TotalFinal > DocumentRules.Portugal.SimplifiedInvoiceMaxTotal || 
     ServicesTotalFinal > DocumentRules.Portugal.SimplifiedInvoiceServicesMaxTotal)) // <-- CORRIGIDO AQUI
{
    string message = GetInvalidSimplifiedInvoiceMessage();
    var response = CustomAlerts.Warning(this)
        .WithSize(new global::System.Drawing.Size(550,440))
        .WithButtonsType(ButtonsType.YesNo)
        .WithMessage(message)
        .ShowAlert();

    if (response != ResponseType.Yes)
    {
        return false;
    }

    // Altera o tipo interno do documento para Fatura-Recibo
    _documentType = "FR";
}


                if (GetDocumentCustomer().FiscalNumber == CustomersService.Default.FiscalNumber && TotalFinal > DocumentRules.Portugal.FinalConsumerMaxTotal)
                {

                    string message = GetInvalidTotalForFinalConsumerMessage();
                    var response = CustomAlerts.Warning(this)
                        .WithSize(new global::System.Drawing.Size(550, 480))
                        .WithMessage(message)
                        .ShowAlert();

                    return false;
                }

            }

            return true;
        }

        protected bool AllFieldsAreValid()
        {
            return ValidatableFields.All(txt => txt.IsValid());
        }

    }
}
