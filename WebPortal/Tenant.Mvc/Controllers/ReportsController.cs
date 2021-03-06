﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web;
using System.Web.Mvc;
using Microsoft.PowerBI.Api.Beta;
using Microsoft.PowerBI.Api.Beta.Models;
using Microsoft.PowerBI.Security;
using Microsoft.Rest;
using Tenant.Mvc.Core.Helpers;
using Tenant.Mvc.Core.Interfaces.Tenant;
using Tenant.Mvc.Models;

namespace Tenant.Mvc.Controllers
{
    public class ReportsController : BaseController
    {
        #region - Fields -

        private const string DefaultReportCode = "DefaultReportId";

        #endregion

        #region - Fields -

        private readonly IApplicationDefaultsRepository _defaultsRepository;

        #endregion

        #region - Controllers -

        public ReportsController(IApplicationDefaultsRepository defaultsRepository)
        {
            // Setup Fields
            _defaultsRepository = defaultsRepository;

            // Setup Callbacks
            _defaultsRepository.StatusCallback = DisplayMessage;
        }

        #endregion

        #region - Index View -

        [HttpGet]
        public ActionResult Index()
        {
            // Get the default report
            var defaultReport = FetchReport(_defaultsRepository.GetApplicationDefault(DefaultReportCode));

            // Build up the view model
            var viewModel = new ReportsViewModel()
            {
                SelectedReportId = new Guid(defaultReport.Report.Id),
                Reports = FetchReports(defaultReport.Report.Id),
                Report = defaultReport.Report,
                AccessToken = defaultReport.AccessToken
            };

            return View(viewModel);
        }

        [HttpPost]
        public ActionResult Index(ReportsViewModel viewModel)
        {
            // Get the selected report
            var reportResult = FetchReport(viewModel.SelectedReportId.ToString());

            // Build up the view model
            viewModel.Reports = FetchReports(viewModel.SelectedReportId.ToString());
            viewModel.Report = reportResult.Report;
            viewModel.AccessToken = reportResult.AccessToken;

            return View(viewModel);
        }

        #endregion

        #region - Upload View -

        [HttpGet]
        public ActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public JsonResult UploadFiles()
        {
            var results = new List<UploadFileViewModel>();

            foreach (string file in Request.Files)
            {
                var postedFile = Request.Files[file];

                if (postedFile == null || postedFile.ContentLength == 0)
                {
                    continue;
                }

                UploadReport(postedFile);

                results.Add(new UploadFileViewModel()
                {
                    Name = postedFile.FileName,
                    Length = postedFile.ContentLength,
                    Type = postedFile.ContentType
                });
            }

            return Json(results, JsonRequestBehavior.AllowGet);
        }

        #endregion

        #region - Private Methods -

        private SelectList FetchReports(string reportId)
        {
            var devToken = PowerBIToken.CreateDevToken(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId);

            using (var client = CreatePowerBIClient(devToken))
            {
                var reportsResponse = client.Reports.GetReports(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId.ToString());

                return new SelectList(reportsResponse.Value.ToList(), "Id", "Name", reportId);
            }
        }

        public FetchReportResult FetchReport(string reportId)
        {
            var devToken = PowerBIToken.CreateDevToken(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId);
            using (var client = CreatePowerBIClient(devToken))
            {
                var reports = client.Reports.GetReports(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId.ToString());
                var report = reports.Value.FirstOrDefault(r => r.Id == reportId);

                var embedToken = PowerBIToken.CreateReportEmbedToken(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId, Guid.Parse(report.Id));

                var result = new FetchReportResult
                {
                    Report = report,
                    AccessToken = embedToken.Generate(ConfigHelper.PowerbiSigningKey)
                };

                return result;
            }
        }

        private void UploadReport(HttpPostedFileBase postedFile)
        {
            // Create a dev token for import
            var devToken = PowerBIToken.CreateDevToken(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId);
            using (var client = CreatePowerBIClient(devToken))
            {
                // Import PBIX file from the file stream
                var import = client.Imports.PostImportWithFile(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId.ToString(), postedFile.InputStream, Path.GetFileNameWithoutExtension(postedFile.FileName));

                // Example of polling the import to check when the import has succeeded.
                while (import.ImportState != "Succeeded" && import.ImportState != "Failed")
                {
                    import = client.Imports.GetImportById(ConfigHelper.PowerbiWorkspaceCollection, ConfigHelper.PowerbiWorkspaceId.ToString(), import.Id);
                    Thread.Sleep(1000);
                }
            }
        }

        private IPowerBIClient CreatePowerBIClient(PowerBIToken token)
        {
            var jwt = token.Generate(ConfigHelper.PowerbiSigningKey);
            var credentials = new TokenCredentials(jwt, "AppToken");

            var client = new PowerBIClient(credentials)
            {
                BaseUri = new Uri(ConfigHelper.PowerbiApiUrl)
            };

            return client;
        }

        #endregion

        #region - Page Helpers -

        [HttpPost]
        public JsonResult UpdateDefaultReport(string reportId)
        {
            _defaultsRepository.SetApplicationDefault(DefaultReportCode, reportId);

            return Json(new { succeeded = true }, JsonRequestBehavior.AllowGet);
        }

        #endregion

        #region - FetchReportResult Class -

        public class FetchReportResult
        {
            public Report Report { get; set; }
            public string AccessToken { get; set; }
        }

        #endregion
    }
}
